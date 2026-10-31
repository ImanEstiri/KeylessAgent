using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Interactive;

/// <summary>
/// The default command (<c>keyless</c>): asks for the workspace path first, then prompts for
/// requests after each finished turn, reusing the same chat. Supports /workspace and /help.
/// Ctrl+C cancels only the current turn; the process exits for an empty request or a startup error.
/// </summary>
internal sealed class InteractiveCommand : AsyncCommand<InteractiveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InteractiveSettings settings, CancellationToken cancellationToken)
    {
        // First input: the workspace path — never a silent current-directory assumption.
        var workspace = WorkspacePrompt.ResolveOrPrompt(settings.Workspace);
        ConsoleWriter.Shared.Header(workspace);
        AnsiConsole.MarkupLine("[grey]Type a request, or press Enter on an empty line to quit. Ctrl+C cancels only the current turn. /help lists slash commands.[/]");

        var sessions = KeylessAgentServiceFactory.CreateSessionStore();

        // /workspace re-runs the git preconditions for the new workspace and starts a fresh
        // chat session for it (its own .agent/session.json); a failure keeps the old workspace.
        async Task<string?> SwitchWorkspace(string oldWorkspace, string newWorkspace, CancellationToken ct)
        {
            if (oldWorkspace == newWorkspace)
            {
                // /newchat: clear the session file so a fresh chat starts
                var resetResult = await sessions.ClearAsync(newWorkspace);
                if (resetResult.IsFailure)
                    return $"Cannot reset the session file: {resetResult.Error.Message}";
                return null;
            }

            var git = KeylessAgentServiceFactory.CreateGitService();
            var repo = await git.GetRepositoryInfoAsync(newWorkspace);
            if (repo.IsFailure || !repo.Data.IsRepository)
                return "The new workspace is not a git repository.";

            var cleared = await sessions.ClearAsync(newWorkspace);
            if (cleared.IsFailure)
                return $"Cannot reset the session file: {cleared.Error.Message}";

            return null;
        }

        var session = new InteractiveSession(
            AnsiConsole.Console,
            (currentWorkspace, request, turnCancellationToken) =>
                AgentCliRunner.RunTurnAsync(currentWorkspace, request, dryRun: false, settings.Verbose, turnCancellationToken, timing: settings.Timing),
            SwitchWorkspace);

        var (exitCode, _) = await session.RunAsync(workspace, cancellationToken);
        return exitCode;
    }
}
