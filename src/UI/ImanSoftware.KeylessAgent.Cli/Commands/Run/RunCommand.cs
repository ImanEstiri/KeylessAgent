using ImanSoftware.KeylessAgent.Cli.Helpers;
using Spectre.Console;
using Spectre.Console.Cli;

namespace ImanSoftware.KeylessAgent.Cli.Commands.Run;

internal sealed class RunCommand : AsyncCommand<RunSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RunSettings settings, CancellationToken cancellationToken)
    {
        // First input: the workspace path — never a silent current-directory assumption.
        var workspace = WorkspacePrompt.ResolveOrPrompt(settings.Workspace);
        var request = string.IsNullOrWhiteSpace(settings.Request)
            ? AnsiConsole.Prompt(new TextPrompt<string>("[cyan]request>[/] What should the agent do?"))
            : settings.Request.Trim();

        ConsoleWriter.Shared.Header(workspace);

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            return await AgentCliRunner.RunTurnAsync(
                workspace, request, settings.DryRun, settings.Verbose, turnCts.Token);
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            // Cancel only the current turn; do not kill the process mid-write.
            eventArgs.Cancel = true;
            turnCts.Cancel();
        }
    }
}
