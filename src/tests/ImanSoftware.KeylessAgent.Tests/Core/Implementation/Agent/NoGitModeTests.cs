using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.KeylessAgent.Core.Implementation.Parsing;
using ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Agent;

/// <summary>
/// NO-GIT MODE: git missing → one warning, no prompts, agent branch skipped, @@commit returns
/// a clear "nothing committed" result without failing the turn.
/// </summary>
public class NoGitModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-nogit-" + Guid.CreateVersion7().ToString("N"));

    public NoGitModeTests()
    {
        Directory.CreateDirectory(_root);
    }

    private static AgentLoop CreateLoop(FakeChatProvider chat, FakeGitService git) =>
        new(chat,
            new ConfigurationService(),
            git,
            new BranchGuard(git),
            new SnapshotService(ImanFileStorageFactory.CreateStorage(), new WorkspaceAnalyzer(ImanFileStorageFactory.CreateStorage())),
            new ResponseFormatGuard(),
            new WorkspaceCommandExecutor(),
            new ResultFormatter(),
            new SessionStore(),
            new WorkspaceInspector(git),
            new ChangeTracker(git, ImanFileStorageFactory.CreateStorage()),
            new BranchNameBuilder(new FakeDateTimeProvider()),
            new FakeDateTimeProvider(),
            null);

    private static string DoneReply(string message) =>
        "@@say\n<<<MSG\nworking\nMSG>>>\n@@end\n@@done\n<<<MSG\n" + message + "\nMSG>>>\n@@end\n@@eof\n";

    [Fact]
    public async Task GitMissing_WarnsOnce_NoPrompts_TurnSucceeds()
    {
        var chat = new FakeChatProvider(DoneReply("done without git"));
        var git = new FakeGitService { GitInstalled = false, IsRepository = false };
        var loop = CreateLoop(chat, git);
        var ui = new FakeAgentUserInterface();
        File.WriteAllText(Path.Combine(_root, "note.txt"), "content");

        var result = await loop.RunAsync(_root, "make a file", ui);

        result.ErrorMessage.Should().BeNull();
        result.CompletedByDone.Should().BeTrue();
        result.Branch.Should().BeEmpty();
        ui.Said.Should().Contain(s => s.Contains("Git is not installed"));
        // no prompts at all (no dirty menu, no branch menu, no confirmations for say/done)
        ui.Menus.Should().BeEmpty();
        ui.Confirmations.Should().BeEmpty();
        // git was never invoked for init
        git.InitCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GitMissing_CommitCommand_ReturnsGitNotInstalledResult_WithoutFailingTheTurn()
    {
        var chat = new FakeChatProvider(
            "@@commit\n<<<MSG\nsave work\nMSG>>>\n@@end\n@@eof\n",
            DoneReply("done"));
        var git = new FakeGitService { GitInstalled = false, IsRepository = false };
        var loop = CreateLoop(chat, git);
        var ui = new FakeAgentUserInterface();

        var result = await loop.RunAsync(_root, "commit it", ui);

        result.ErrorMessage.Should().BeNull("no-git @@commit is a reported result, not a turn failure");
        result.CompletedByDone.Should().BeTrue();
        var commitResult = chat.Sent.FirstOrDefault(m => m.Text.StartsWith("[KEYLESSAGENT RESULTS]", StringComparison.Ordinal));
        commitResult.Text.Should().Contain("git unavailable, nothing committed");
    }

    [Fact]
    public async Task GitMissing_SystemPromptSaysGitUnavailable()
    {
        var chat = new FakeChatProvider(DoneReply("done"));
        var git = new FakeGitService { GitInstalled = false, IsRepository = false };
        var loop = CreateLoop(chat, git);

        await loop.RunAsync(_root, "task", new FakeAgentUserInterface());

        chat.Sent[0].Text.Should().Contain("git is NOT installed");
        chat.Sent[0].Text.Should().Contain("do not use @@commit");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
