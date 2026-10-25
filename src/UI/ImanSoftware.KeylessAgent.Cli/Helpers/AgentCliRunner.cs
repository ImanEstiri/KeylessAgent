using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Factory;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli.Helpers;

/// <summary>
/// Shared turn runner for the CLI commands. The turn itself is NOT wrapped in a live display:
/// waits (chat reply, running processes, snapshot scripts) get their own spinner through
/// <see cref="IAgentUserInterface.BeginWait(string)"/>; everything else is plain output.
/// A failing or cancelled turn is reported, never fatal to the process.
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
    /// <param name="cancellationToken">Cancels the current turn (Ctrl+C).</param>
    /// <returns>0 on a completed turn, non-zero on failure or cancellation.</returns>
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
        var ui = new ConsoleUserInterface();
        ConsoleWriter.Info(verbose
            ? "Verbose diagnostics: .agent/logs/diagnostics.log"
            : "Ctrl+C cancels this turn.");

        try
        {
            var result = await loop.RunAsync(workspaceRoot, request, ui, dryRun, cancellationToken);

            AnsiConsole.WriteLine();
            if (result.ErrorMessage is not null)
            {
                ConsoleWriter.Error(result.ErrorMessage);
                return 1;
            }

            if (result.StoppedReason is not null)
                ConsoleWriter.Warning($"Turn stopped early: {result.StoppedReason}");

            ConsoleWriter.Success($"Turn {result.Turn} finished on branch {result.Branch}");

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
        catch (OperationCanceledException)
        {
            ConsoleWriter.Warning("Turn cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            ConsoleWriter.Error($"Turn failed: {ex.Message}");
            ConsoleWriter.Info("Details are in .agent/logs (turn transcript; diagnostics.log with -v).");
            return 1;
        }
    }
}
