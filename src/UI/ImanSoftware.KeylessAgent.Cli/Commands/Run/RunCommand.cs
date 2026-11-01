using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
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

        // One explicit tool status per session: git/dotnet/python/node with download pages
        // for anything missing (served from the same 24 h cache the agent loop uses).
        var environmentTools = KeylessAgentServiceFactory.CreateEnvironmentTools();
        var tools = await environmentTools.GetToolsCachedAsync(workspace, settings.RefreshEnv, cancellationToken);
        ConsoleWriter.Shared.Info(IEnvironmentTools.FormatToolsLine(tools));
        ConsoleWriter.Shared.Info($"Pace: {settings.Pace?.ToString() ?? "Balanced (default)"} — non-say commands per response: " +
            (settings.Pace == ImanSoftware.KeylessAgent.Abstractions.Models.Enums.AgentPace.Step ? "1" : settings.Pace == ImanSoftware.KeylessAgent.Abstractions.Models.Enums.AgentPace.Batch ? "Limits.MaxCommandsPerResponse" : "3"));

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            return await AgentCliRunner.RunTurnAsync(
                workspace, request, settings.DryRun, settings.Verbose, turnCts.Token, timing: settings.Timing,
                refreshEnv: settings.RefreshEnv, paceOverride: settings.Pace);
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
