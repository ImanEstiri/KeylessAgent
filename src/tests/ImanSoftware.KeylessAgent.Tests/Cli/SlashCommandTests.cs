using FluentAssertions;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using Spectre.Console;
using Spectre.Console.Testing;

namespace ImanSoftware.KeylessAgent.Tests.Cli;

/// <summary>
/// Slash commands in the interactive session: /help lists commands, unknown /-prefixed
/// input is rejected with a hint (never sent to the model), and /workspace switches to a
/// validated directory — Core always receives the workspace explicitly.
/// </summary>
public class SlashCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-slash-" + Guid.CreateVersion7().ToString("N"));

    public SlashCommandTests()
    {
        Directory.CreateDirectory(_root);
    }

    private static (List<string> TurnWorkspaces, List<string> TurnRequests, List<(string Old, string New)> Switches)
        Recordings()
        => (new List<string>(), new List<string>(), new List<(string, string)>());

    [Fact]
    public async Task Help_ListsSlashCommands_WithoutSendingAnythingToTheModel()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("/help");
        console.Input.PushTextWithEnter("");
        var recordings = Recordings();

        var session = new InteractiveSession(console,
            (ws, req, _) =>
            {
                recordings.TurnWorkspaces.Add(ws);
                recordings.TurnRequests.Add(req);
                return Task.FromResult(0);
            },
            (_, _) => null);
        var (_, workspace) = await session.RunAsync(_root, CancellationToken.None);

        console.Output.Should().Contain("/workspace");
        console.Output.Should().Contain("/help");
        recordings.TurnRequests.Should().BeEmpty("a slash command is never sent to the model");
        workspace.Should().Be(_root);
    }

    [Fact]
    public async Task UnknownSlashCommand_IsRejectedWithHint()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("/deploy everything");
        console.Input.PushTextWithEnter("");
        var recordings = Recordings();

        var session = new InteractiveSession(console,
            (_, req, _) =>
            {
                recordings.TurnRequests.Add(req);
                return Task.FromResult(0);
            },
            (_, _) => null);
        await session.RunAsync(_root, CancellationToken.None);

        console.Output.Should().Contain("Unknown command");
        console.Output.Should().Contain("/workspace <path>, /help");
        recordings.TurnRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Workspace_SwitchesToValidatedDirectory_AndRecordsNewWorkspaceForTurns()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter($"/workspace {_root}");
        console.Input.PushTextWithEnter("add a test");
        console.Input.PushTextWithEnter("");
        var recordings = Recordings();
        var switched = new List<(string Old, string New)>();

        var session = new InteractiveSession(console,
            (ws, req, _) =>
            {
                recordings.TurnWorkspaces.Add(ws);
                recordings.TurnRequests.Add(req);
                return Task.FromResult(0);
            },
            (oldWs, newWs) =>
            {
                switched.Add((oldWs, newWs));
                return null; // preconditions passed
            });
        var (_, workspace) = await session.RunAsync(_root, CancellationToken.None);

        switched.Should().ContainSingle().Which.New.Should().Be(_root);
        recordings.TurnWorkspaces.Should().Contain(_root);
        workspace.Should().Be(_root);
    }

    [Fact]
    public async Task Workspace_ToMissingDirectory_IsRefused()
    {
        var console = new TestConsole();
        var missing = Path.Combine(_root, "does-not-exist");
        console.Input.PushTextWithEnter($"/workspace {missing}");
        console.Input.PushTextWithEnter("");
        var recordings = Recordings();

        var session = new InteractiveSession(console,
            (_, _, _) => Task.FromResult(0),
            (_, _) => null);
        await session.RunAsync(_root, CancellationToken.None);

        console.Output.Should().Contain("Directory not found");
        recordings.TurnRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Workspace_SwitchFailure_KeepsTheOldWorkspace()
    {
        var console = new TestConsole();
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        console.Input.PushTextWithEnter($"/workspace {other}");
        console.Input.PushTextWithEnter("a request");
        console.Input.PushTextWithEnter("");
        var turnWorkspaces = new List<string>();

        var session = new InteractiveSession(console,
            (ws, _, _) =>
            {
                turnWorkspaces.Add(ws);
                return Task.FromResult(0);
            },
            (_, _) => "The new workspace is not a git repository.");
        var (_, workspace) = await session.RunAsync(_root, CancellationToken.None);

        console.Output.Should().Contain("The new workspace is not a git repository");
        turnWorkspaces.Should().Contain(_root, "the failed switch keeps the old workspace");
        workspace.Should().Be(_root);
    }

    [Fact]
    public async Task Workspace_WithoutPath_ShowsUsage()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("/workspace");
        console.Input.PushTextWithEnter("");

        var session = new InteractiveSession(console,
            (_, _, _) => Task.FromResult(0),
            (_, _) => null);
        await session.RunAsync(_root, CancellationToken.None);

        console.Output.Should().Contain("Usage: /workspace <path>");
        console.Output.Should().Contain(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
