using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.KeylessAgent.Core.Implementation.Parsing;
using ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Agent;

public class AgentLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-loop-" + Guid.CreateVersion7().ToString("N"));
    private readonly IFileStorage _storage = ImanFileStorageFactory.CreateStorage();

    public AgentLoopTests()
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

    private static AgentLoop CreateLoop(FakeChatProvider chat) =>
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

    private static string DoneReply(string message) =>
        "@@say\n<<<MSG\nworking\nMSG>>>\n@@end\n@@done\n<<<MSG\n" + message + "\nMSG>>>\n@@end\n@@eof\n";

    [Fact]
    public async Task RunAsync_HappyPath_ExecutesCommandsAndRefreshesSnapshotAfterTurn()
    {
        var chat = new FakeChatProvider(
            """
            @@say
            <<<MSG
            creating the file
            MSG>>>
            @@end
            @@newfile src/Generated.cs
            <<<CONTENT
            class Generated {}
            CONTENT>>>
            @@end
            @@done
            <<<MSG
            created src/Generated.cs
            MSG>>>
            @@end
            @@eof
            """);
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        var result = await loop.RunAsync(_root, "add a generated class", ui);

        result.ErrorMessage.Should().BeNull();
        result.CompletedByDone.Should().BeTrue();
        result.Summary.Should().Contain("created src/Generated.cs");
        result.Branch.Should().StartWith("agent/");
        _storage.FileExists(Path.Combine(_root, "src", "Generated.cs")).Should().BeTrue();

        // The snapshot ran after the turn ended: the fresh tree contains the file created during the turn.
        var tree = await _storage.ReadAllTextAsync(Path.Combine(_root, "tree-view-result.txt"));
        tree.Data.Should().Contain("Generated.cs");

        // Message 1 = the AgentScript rules alone; message 2 = the user request + attachments.
        chat.Sent.Should().HaveCount(2);
        chat.Sent[0].Text.Should().Contain("AgentScript v1");
        chat.Sent[0].Text.Should().NotContain("add a generated class");
        chat.Sent[1].Text.Should().Contain("add a generated class");
        chat.Sent[1].AttachmentNames.Should().Contain("tree-view-result.txt");
        ui.Said.Should().Contain("creating the file");
    }

    [Fact]
    public async Task RunAsync_InvalidThenValidReply_SendsCorrectionThenSucceeds()
    {
        var chat = new FakeChatProvider(
            "Sure! I will add the file for you right away.",
            DoneReply("recovered after the correction"));
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        var result = await loop.RunAsync(_root, "do something", ui);

        result.CompletedByDone.Should().BeTrue();
        chat.Sent.Should().Contain(m => m.Text.StartsWith("[KEYLESSAGENT FORMAT ERROR]", StringComparison.Ordinal));
        var correction = chat.Sent.First(m => m.Text.StartsWith("[KEYLESSAGENT FORMAT ERROR]", StringComparison.Ordinal));
        correction.Text.Should().Contain("Problems:");
        correction.Text.Should().Contain("Resend your COMPLETE response");
    }

    [Fact]
    public async Task RunAsync_FormatRetriesExhausted_StopsAndShowsRawReply()
    {
        var chat = new FakeChatProvider("invalid one", "invalid two", "invalid three", "invalid four");
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        var result = await loop.RunAsync(_root, "task", ui);

        result.CompletedByDone.Should().BeFalse();
        result.StoppedReason.Should().Contain("format retries exhausted");
        ui.Said.Should().Contain(s => s.Contains("invalid four"));
    }

    [Fact]
    public async Task RunAsync_CommandLimit_ExecutesFirstNAndReportsSkipped()
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"),
            """{ "Limits": { "MaxCommandsPerResponse": 2 }, "Browser": { "MaxMessagesPerMinute": 1000, "MinDelayMs": 0, "MaxDelayMs": 0 } }""");
        var chat = new FakeChatProvider(
            """
            @@newfile a1.txt
            <<<CONTENT
            1
            CONTENT>>>
            @@end
            @@newfile a2.txt
            <<<CONTENT
            2
            CONTENT>>>
            @@end
            @@newfile a3.txt
            <<<CONTENT
            3
            CONTENT>>>
            @@end
            @@eof
            """);
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        var result = await loop.RunAsync(_root, "create three files", ui);

        _storage.FileExists(Path.Combine(_root, "a1.txt")).Should().BeTrue();
        _storage.FileExists(Path.Combine(_root, "a2.txt")).Should().BeTrue();
        _storage.FileExists(Path.Combine(_root, "a3.txt")).Should().BeFalse();

        var results = chat.Sent.FirstOrDefault(m => m.Text.StartsWith("[KEYLESSAGENT RESULTS]", StringComparison.Ordinal));
        results.Should().NotBeNull();
        results.Text.Should().Contain("--- skipped 1 commands: batch limit is 2 per response, resend them next ---");
        result.StoppedReason.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_InAutoSafe_ShowsSecurityBoundaryWarning()
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"),
            """{ "Mode": "AutoSafe" }""");
        var chat = new FakeChatProvider(DoneReply("done"));
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        await loop.RunAsync(_root, "auto task", ui);

        ui.Said.Should().Contain(s => s.Contains("AutoSafe is not a security boundary"));
    }

    [Fact]
    public async Task RunAsync_DryRun_ExecutesNothing()
    {
        var chat = new FakeChatProvider(
            """
            @@newfile dry.txt
            <<<CONTENT
            x
            CONTENT>>>
            @@end
            @@done
            <<<MSG
            done
            MSG>>>
            @@end
            @@eof
            """);
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface { AskAnswer = "continue" };

        var result = await loop.RunAsync(_root, "dry task", ui, dryRun: true);

        _storage.FileExists(Path.Combine(_root, "dry.txt")).Should().BeFalse();
        result.DryRun.Should().BeTrue();
        result.CompletedByDone.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_OutsideGitRepository_WithAutoInitDisabled_FailsFast()
    {
        var notARepo = Path.Combine(Path.GetTempPath(), "ka-norepo-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(notARepo);
        try
        {
            File.WriteAllText(Path.Combine(notARepo, "keyless.json"),
                "{ \"Git\": { \"AutoInit\": false } }");
            var loop = CreateLoop(new FakeChatProvider(DoneReply("x")));
            var ui = new FakeAgentUserInterface();

            var result = await loop.RunAsync(notARepo, "task", ui);

            result.ErrorMessage.Should().Contain("not a git repository");
            result.ErrorCode.Should().Be("KA_NOT_A_GIT_REPOSITORY");
        }
        finally
        {
            Directory.Delete(notARepo, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_OutsideGitRepository_WithAutoInit_InitializesAndContinues()
    {
        var notARepo = Path.Combine(Path.GetTempPath(), "ka-autoinit-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(notARepo);
        File.WriteAllText(Path.Combine(notARepo, "tree-view.bat"), "@echo off\r\ntree /F /A > tree-view-result.txt\r\n");
        File.WriteAllText(Path.Combine(notARepo, "code-dump.bat"), "@echo off\r\necho ### code-dump-result > code-dump-result.md\r\n");
        try
        {
            var chat = new FakeChatProvider(DoneReply("initialized and done"));
            var loop = CreateLoop(chat);
            var ui = new FakeAgentUserInterface();

            var result = await loop.RunAsync(notARepo, "init me", ui);

            result.ErrorMessage.Should().BeNull();
            result.CompletedByDone.Should().BeTrue();
            result.Branch.Should().StartWith("agent/");
            // The agent branch was created from the fresh initial commit.
            result.Commits.Should().NotBeEmpty();
        }
        finally
        {
            TryDelete(notARepo);
        }
    }

    /// <summary>git object files are read-only; delete best-effort.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, System.IO.FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public async Task RunAsync_SessionPersistsChatUrlAndTurn()
    {
        var chat = new FakeChatProvider(DoneReply("first turn done"));
        var loop = CreateLoop(chat);

        await loop.RunAsync(_root, "first request", new FakeAgentUserInterface());

        var sessionJson = await _storage.ReadAllTextAsync(Path.Combine(_root, ".agent", "session.json"));
        sessionJson.IsSuccess.Should().BeTrue();
        sessionJson.Data.Should().Contain("\"Turn\": 1");
        sessionJson.Data.Should().Contain("fake-session");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
