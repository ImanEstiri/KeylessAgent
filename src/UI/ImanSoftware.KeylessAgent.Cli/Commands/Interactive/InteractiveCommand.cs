using ImanSoftware.KeylessAgent.Cli.Helpers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Interactive;

/// <summary>
/// The default command (<c>keyless</c>): asks for the workspace path first, then prompts for
/// requests after each finished turn, reusing the same chat. Ctrl+C cancels only the current
/// turn; the process exits for an empty request or a startup error.
/// </summary>
internal sealed class InteractiveCommand : AsyncCommand<InteractiveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InteractiveSettings settings, CancellationToken cancellationToken)
    {
        // First input: the workspace path — never a silent current-directory assumption.
        var workspace = WorkspacePrompt.ResolveOrPrompt(settings.Workspace);
        ConsoleWriter.Shared.Header(workspace);
        AnsiConsole.MarkupLine("[grey]Type a request, or press Enter on an empty line to quit. Ctrl+C cancels only the current turn.[/]");

        var session = new InteractiveSession(
            AnsiConsole.Console,
            (request, turnCancellationToken) =>
                AgentCliRunner.RunTurnAsync(workspace, request, dryRun: false, settings.Verbose, turnCancellationToken));

        return await session.RunAsync(cancellationToken);
    }
}
