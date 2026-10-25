using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Factory;
using ImanSoftware.KeylessAgent.Core.Implementation.Diagnostics;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli.Helpers;

/// <summary>
/// Shared turn runner for the CLI commands: spinner while the turn runs, branch summary at the end.
/// </summary>
internal static class AgentCliRunner
{
    /// <summary>
    /// Runs one agent turn with the real browser chat provider.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="request">The user's request.</param>
    /// <param name="dryRun">Dry-run mode.</param>
    /// <param name="verbose">Verbose (debug) diagnostics logging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>0 on a completed turn, 1 on failure.</returns>
    public static async Task<int> RunTurnAsync(
        string workspaceRoot,
        string request,
        bool dryRun,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        ILoggerFactory? loggerFactory = null;
        if (verbose)
            loggerFactory = KeylessAgentServiceFactory.CreateFileLoggerFactory(workspaceRoot);

        var loop = KeylessAgentServiceFactory.CreateAgentLoop(loggerFactory);
        var ui = new SpectreAgentUserInterface();
        ConsoleWriter.Info(verbose ? "Verbose diagnostics: .agent/logs/diagnostics.log" : "Ctrl+C cancels the turn.");

        var result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Starting the agent…", async status =>
            {
                ui.AttachStatus(status);
                return await loop.RunAsync(workspaceRoot, request, ui, dryRun, cancellationToken);
            });

        AnsiConsole.WriteLine();
        if (result.ErrorMessage is not null)
        {
            ConsoleWriter.Error(result.ErrorMessage);
            return 1;
        }

        if (result.StoppedReason is not null)
            ConsoleWriter.Warning($"Turn stopped early: {result.StoppedReason}");

        ConsoleWriter.Success($"Turn {result.Turn} finished on branch [white]{result.Branch.EscapeMarkup()}[/]");

        if (result.Commits.Count > 0)
        {
            AnsiConsole.MarkupLine("[grey]Commits:[/]");
            foreach (var commit in result.Commits)
                AnsiConsole.MarkupLine($"  [grey]•[/] {commit.EscapeMarkup()}");
        }

        if (result.Summary is not null)
            ConsoleWriter.Info(result.Summary);

        return 0;
    }
}
