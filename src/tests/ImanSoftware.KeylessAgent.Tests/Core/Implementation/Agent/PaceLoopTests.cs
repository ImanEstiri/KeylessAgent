using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
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

public class PaceLoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-pace-" + Guid.CreateVersion7().ToString("N"));

    public PaceLoopTests()
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
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* the snapshot bat processes may hold the folder briefly */ }
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

    private const string SixFilesReply = """
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
        @@newfile a4.txt
        <<<CONTENT
        4
        CONTENT>>>
        @@end
        @@newfile a5.txt
        <<<CONTENT
        5
        CONTENT>>>
        @@end
        @@newfile a6.txt
        <<<CONTENT
        6
        CONTENT>>>
        @@end
        @@eof
        """;

    public static TheoryData<Abstractions.Models.Enums.AgentPace, int, string> EffectiveLimitTable() => new()
    {
        { Abstractions.Models.Enums.AgentPace.Step, 1, "pace=Step: only 1 command(s) per response; send the next one after reading this result" },
        { Abstractions.Models.Enums.AgentPace.Balanced, 3, "pace=Balanced: only 3 command(s) per response; send the next one after reading this result" },
        { Abstractions.Models.Enums.AgentPace.Batch, 5, "batch limit is 5 per response, resend them next" },
    };

    [Theory]
    [MemberData(nameof(EffectiveLimitTable))]
    public async Task RunAsync_PaceLimit_ExecutesTheFirstN_AndReportsTheRest(
        Abstractions.Models.Enums.AgentPace pace, int expectedExecuted, string expectedSkipText)
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"),
            "{ \"Agent\": { \"Pace\": \"" + pace + "\" }, \"Limits\": { \"MaxCommandsPerResponse\": 5 }, " +
            "\"Browser\": { \"MaxMessagesPerMinute\": 1000, \"MinDelayMs\": 0, \"MaxDelayMs\": 0 } }");
        var chat = new FakeChatProvider(SixFilesReply);
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        await loop.RunAsync(_root, "create three files", ui);

        File.Exists(Path.Combine(_root, "a1.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "a2.txt")).Should().Be(expectedExecuted >= 2);
        File.Exists(Path.Combine(_root, "a3.txt")).Should().Be(expectedExecuted >= 3);
        var results = chat.Sent.FirstOrDefault(m => m.Text.StartsWith("[KEYLESSAGENT RESULTS]", StringComparison.Ordinal));
        results.Should().NotBeNull();
        results!.Text.Should().Contain(expectedSkipText);
    }

    [Fact]
    public async Task RunAsync_StepPace_PlanProgressAndVerifiedLines_EndToEnd()
    {
        File.WriteAllText(Path.Combine(_root, "keyless.json"),
            "{ \"Agent\": { \"Pace\": \"Step\" }, \"Browser\": { \"MaxMessagesPerMinute\": 1000, \"MinDelayMs\": 0, \"MaxDelayMs\": 0 } }");
        var chat = new FakeChatProvider("""
            @@say
            <<<MSG
            Plan:
            1. Create login.html
            2. Create login.css
            MSG>>>
            @@end
            @@newfile login.html
            <<<CONTENT
            <html></html>
            CONTENT>>>
            @@end
            @@next
            @@end
            @@eof
            """,
            """
            @@newfile login.css
            <<<CONTENT
            body {}
            CONTENT>>>
            @@end
            @@next
            @@end
            @@eof
            """,
            """
            @@done
            <<<MSG
            Both files created.
            MSG>>>
            @@end
            @@eof
            """);
        var loop = CreateLoop(chat);
        var ui = new FakeAgentUserInterface();

        var result = await loop.RunAsync(_root, "create login.html and login.css", ui);

        result.CompletedByDone.Should().BeTrue();
        File.Exists(Path.Combine(_root, "login.html")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "login.css")).Should().BeTrue();

        var resultsMessages = chat.Sent
            .Where(m => m.Text.StartsWith("[KEYLESSAGENT RESULTS]", StringComparison.Ordinal))
            .ToList();
        resultsMessages.Should().HaveCount(2, "the @@done response ends the turn without a results message");
        resultsMessages[0].Text.Should().Contain("verified: path=login.html bytes=");
        resultsMessages[0].Text.Should().Contain("sha=");
        resultsMessages[0].Text.Should().Contain("first=\"<html></html>\"");
        resultsMessages[0].Text.Should().Contain("last=\"<html></html>\"");
        resultsMessages[1].Text.Should().Contain("verified: path=login.css bytes=");

        ui.Progress.Should().Contain(p => p.Detail.StartsWith("Step 1/2:"));
        ui.Progress.Should().Contain(p => p.Detail.StartsWith("Step 2/2:"));
    }
}
