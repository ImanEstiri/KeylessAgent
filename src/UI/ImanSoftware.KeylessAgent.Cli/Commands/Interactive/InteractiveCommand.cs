using ImanSoftware.KeylessAgent.Cli.Helpers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Interactive;

/// <summary>
/// The default command (<c>keyless</c> with no arguments): asks for the workspace path first,
/// then prompts for requests after each finished turn, reusing the same chat.
/// </summary>
internal sealed class InteractiveCommand : AsyncCommand<InteractiveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InteractiveSettings settings, CancellationToken cancellationToken)
    {
        // First input: the workspace path — never a silent current-directory assumption.
        var workspace = WorkspacePrompt.ResolveOrPrompt(settings.Workspace);
        ConsoleWriter.Header(workspace);
        AnsiConsole.MarkupLine("[grey]Type a request, or press Enter on an empty line to quit. Each finished turn keeps the same chat.[/]");

        while (!cancellationToken.IsCancellationRequested)
        {
            var request = await AnsiConsole.PromptAsync(
                new TextPrompt<string>("[cyan]keyless>[/]").AllowEmpty(),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(request))
                break;

            var exitCode = await AgentCliRunner.RunTurnAsync(workspace, request.Trim(), dryRun: false, settings.Verbose, cancellationToken);
            if (exitCode != 0)
                return exitCode;

            AnsiConsole.WriteLine();
        }

        return 0;
    }
}
