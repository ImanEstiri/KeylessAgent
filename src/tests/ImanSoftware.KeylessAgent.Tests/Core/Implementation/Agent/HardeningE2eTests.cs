using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.KeylessAgent.Core.Implementation.Parsing;
using ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
using ImanSoftware.KeylessAgent.Tests.Fakes;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Agent;

/// <summary>
/// End-to-end hardening scenarios over the real loop with the scripted fake chat provider:
/// research loops with failing probes, move + undo, cancellation mid-turn and the
/// tool-missing fail fast at loop level.
/// </summary>
public class HardeningE2eTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-hard-" + Guid.CreateVersion7().ToString("N"));

    public HardeningE2eTests()
    {
        Directory.CreateDirectory(_root);
        RunGit("init -b main");
        RunGit("config user.email agent@example.com");
        RunGit("config user.name keyless-agent");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# workspace");
        File.WriteAllText(Path.Combine(_root, "tree-view.bat"), "@echo off\r\ntree /F /A > tree-view-result.txt\r\n");
        File.WriteAllText(Path.Combine(_root, "code-dump.bat"), "@echo off\r\necho ### code-dump-result > code-dump-result.md\r\n");
        RunGit("add -A");
        RunGit("commit -m init");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* bat processes may hold the folder briefly */ }
    }

    private void RunGit(string args)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit(30_000);
    }

    private static AgentLoop CreateLoop(IChatProvider chat) =>
        new(chat,
            new ConfigurationService(),
            new GitService(),
            new BranchGuard(),
            new SnapshotService(ImanFileStorageFactory.CreateStorage(), new WorkspaceAnalyzer(ImanFileStorageFactory.CreateStorage())),
            new ResponseFormatGuard(),
            new WorkspaceCommandExecutor(),
            new ResultFormatter(),
            new SessionStore(),
            new WorkspaceInspector(),
            new ChangeTracker(),
            new BranchNameBuilder(new FakeDateTimeProvider()),
            new FakeDateTimeProvider(),
            null);

    [Fact]
    public async Task ResearchLoop_FailingProbe_DoesNotStopTheFollowingCommand()
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"),
            "{ \"Mode\": \"Auto\", \"Commands\": { \"BatchFailureMode\": \"Continue\" }, \"Browser\": { \"MaxMessagesPerMinute\": 1000, \"MinDelayMs\": 0, \"MaxDelayMs\": 0 } }");
        var chat = new FakeChatProvider("""
            @@runcommand
            <<<CMD
            definitely-missing-tool-xyz --version
            CMD>>>
            @@end
            @@runcommand
            <<<CMD
            echo research-step-ok
            CMD>>>
            @@end
            @@next
            @@end
            @@eof
            """,
            """
            @@done
            <<<MSG
            Research finished.
            MSG>>>
            @@end
            @@eof
            """);
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        var result = await loop.RunAsync(_root, "probe the environment", ui);
        foreach (var s in chat.Sent)
            Console.WriteLine("###D>>> " + s.Text[..Math.Min(200, s.Text.Length)].Replace(Environment.NewLine, " | "));
        Console.WriteLine("###RES>>> done={0} stopped={1}", result.CompletedByDone, result.StoppedReason ?? "-");
        foreach (var s in chat.Sent)
            Console.WriteLine("###D1>>> " + s.Text[..Math.Min(500, s.Text.Length)].Replace(Environment.NewLine, " | "));

        result.CompletedByDone.Should().BeTrue("a failed read-only probe must not stop the batch");
        chat.Sent.Any(m => m.Text.StartsWith("[KEYLESSAGENT RESULTS]", StringComparison.Ordinal) && m.Text.Contains("'definitely-missing-tool-xyz' is not available")).Should().BeTrue("the failed probe is reported");
        chat.Sent.Any(m => m.Text.StartsWith("[KEYLESSAGENT RESULTS]", StringComparison.Ordinal) && m.Text.Contains("research-step-ok")).Should().BeTrue("the following command ran after the failed probe");
    }

    [Fact]
    public async Task MoveFile_ThenUndo_RestoresTheOriginalFile()
    {
        File.WriteAllText(Path.Combine(_root, "src-old.txt"), "precious content");
        var chat = new FakeChatProvider("""
            @@movefile src-old.txt
            <<<TO
            src-new.txt
            TO>>>
            @@end
            @@eof
            """);
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        var result = await loop.RunAsync(_root, "rename src-old.txt", ui);

        File.Exists(Path.Combine(_root, "src-old.txt")).Should().BeFalse("the file was moved");
        File.ReadAllText(Path.Combine(_root, "src-new.txt")).Should().Be("precious content");

        var undo = await new BackupService(ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider()).UndoLatestAsync(_root);
        undo.IsSuccess.Should().BeTrue(undo.IsFailure ? undo.Error.Message : "");
        File.Exists(Path.Combine(_root, "src-old.txt")).Should().BeTrue("keyless undo restores the moved-away file");
    }

    [Fact]
    public async Task CancelMidTurn_ReportsCancelled_AndStopsCleanly()
    {
        var chat = new FakeChatProvider("""
            @@say
            <<<MSG
            working on it
            MSG>>>
            @@end
            @@eof
            """);
        var loop = CreateLoop(chat);
        var cts = new CancellationTokenSource();

        // Cancel while the first reply wait is running: the say above arrives, then nothing.
        var ui = new CancelAfterFirstSayUi(cts);

        var result = await loop.RunAsync(_root, "long task", ui, cancellationToken: cts.Token);

        result.ErrorCode.Should().Be("KA_CANCELLED", "the dirty-tree cancel path must carry the stable cancelled code");
        result.ErrorMessage.Should().Contain("Cancelled by the user");
    }

    [Fact]
    public async Task MissingTool_AtLoopLevel_FailsTheCommand_AndTheTurnContinuesToDone()
    {
        // A pre-written env cache marks dotnet as MISSING (checked now, so the 1 h freshness
        // rule serves it): the executor must fail the command fast with KA_TOOL_MISSING.
        // BatchFailureMode Continue lets the turn continue to @@done afterwards.
        File.WriteAllText(Path.Combine(_root, "keyless.json"),
            "{ \"Mode\": \"Auto\", \"Commands\": { \"BatchFailureMode\": \"Continue\" }, \"Browser\": { \"MaxMessagesPerMinute\": 1000, \"MinDelayMs\": 0, \"MaxDelayMs\": 0 } }");
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        Directory.CreateDirectory(Path.Combine(_root, ".agent"));
        File.WriteAllText(Path.Combine(_root, ".agent", "env.json"),
            "{ \"CreatedAtUtc\": \"" + now + "\", \"Block\": \"[ENVIRONMENT][/ENVIRONMENT]\", \"Summary\": \"\", " +
            "\"Tools\": { \"dotnet\": { \"Tool\": \"dotnet\", \"State\": \"missing\", \"Version\": \"\", \"Command\": \"\", \"CheckedAtUtc\": \"" + now + "\" } } }");
        var chat = new FakeChatProvider("""
            @@runcommand
            <<<CMD
            dotnet build
            CMD>>>
            @@end
            @@next
            @@end
            @@eof
            """,
            """
            @@say
            <<<MSG
            continued after the missing tool
            MSG>>>
            @@end
            @@done
            <<<MSG
            finished despite the missing tool
            MSG>>>
            @@end
            @@eof
            """);
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        var result = await loop.RunAsync(_root, "try the missing tool", ui);

        result.CompletedByDone.Should().BeTrue("the tool_missing failure is a normal failed command");
        chat.Sent.Any(m => m.Text.StartsWith("[KEYLESSAGENT RESULTS]", StringComparison.Ordinal) && m.Text.Contains("Do not retry it")).Should().BeTrue("the tool-missing failure is reported to the model");
    }

    /// <summary>A UI that cancels the turn token right after the first say reaches the user.</summary>
    private sealed class CancelAfterFirstSayUi(CancellationTokenSource cts) : IAgentUserInterface
    {
        private int _says;

        public void Say(string message)
        {
            if (Interlocked.Increment(ref _says) == 1)
                cts.Cancel();
        }

        public void ShowProgress(AgentProgressEvent progressEvent) { }

        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(false);

        public Task<string> AskAsync(string question) => Task.FromResult(string.Empty);

        public void ShowDiff(FileChange change) { }

        public Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(ChoiceResult.CancelledMenu());

        public IDisposable BeginWait(string message) => new NoopWait();

        private sealed class NoopWait : IDisposable
        {
            public void Dispose() { }
        }
    }
}
