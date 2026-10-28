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
    /// <param name="timing">Print the per-phase timing table after the turn.</param>
    /// <param name="chatProvider">Optional chat provider override (tests); defaults to the real browser provider.</param>
    /// <returns>0 on a completed turn, non-zero on failure or cancellation.</returns>
    public static async Task<int> RunTurnAsync(
        string workspaceRoot,
        string request,
        bool dryRun,
        bool verbose,
        CancellationToken cancellationToken = default,
        bool timing = false,
        IChatProvider? chatProvider = null)
    {
        ILoggerFactory? loggerFactory = null;
        if (verbose)
            loggerFactory = KeylessAgentServiceFactory.CreateFileLoggerFactory(workspaceRoot);

        var loop = chatProvider is null
            ? KeylessAgentServiceFactory.CreateAgentLoop(loggerFactory)
            : KeylessAgentServiceFactory.CreateAgentLoop(chatProvider, loggerFactory);
        var ui = new ConsoleUserInterface();
        var writer = ConsoleWriter.Shared;
        var logPath = System.IO.Path.Combine(workspaceRoot, ".agent", "logs");
        writer.Info(verbose
            ? "Verbose diagnostics: " + System.IO.Path.Combine(logPath, "diagnostics.log")
            : "Ctrl+C cancels this turn.");

        try
        {
            var result = await loop.RunAsync(workspaceRoot, request, ui, dryRun, cancellationToken);

            AnsiConsole.WriteLine();
            if (result.ErrorMessage is not null)
            {
                writer.Error(result.ErrorMessage);
                return 1;
            }

            if (result.StoppedReason is not null)
                writer.Warning($"Turn stopped early: {result.StoppedReason}");

            writer.Success($"Turn {result.Turn} finished on branch {result.Branch}");

            if (result.Commits.Count > 0)
            {
                AnsiConsole.MarkupLine("[grey]Commits:[/]");
                foreach (var commit in result.Commits)
                    AnsiConsole.MarkupLine($"  [grey]•[/] {commit.EscapeMarkup()}");
            }

            if (result.Summary is not null)
                writer.Info(result.Summary);

            if (result.ChangeSet.Count > 0)
            {
                AnsiConsole.MarkupLine("[grey]Changed files:[/]");
                foreach (var change in result.ChangeSet)
                    AnsiConsole.MarkupLine($"  [grey]•[/] {change.Kind,-8} {change.Path.EscapeMarkup()}");
            }

            if (result.DiffStat.Length > 0)
            {
                AnsiConsole.MarkupLine("[grey]Diff stat:[/]");
                foreach (var line in result.DiffStat.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
                    AnsiConsole.MarkupLine($"  [grey]{line.TrimEnd().EscapeMarkup()}[/]");
            }

            AnsiConsole.MarkupLine($"[grey]Auto-approved: {result.AutoApprovedCommands} | Confirmed: {result.ConfirmedCommands}[/]");
            AnsiConsole.MarkupLine("[grey]Merge the agent branch yourself; keyless does not merge.[/]");

            if (timing && result.Timings is not null)
                AnsiConsole.WriteLine(result.Timings.TrimEnd());

            AnsiConsole.MarkupLine("[grey]keyless undo restores .agent/backups; changes made by commands are NOT undoable with keyless undo.[/]");

            return 0;
        }
        catch (OperationCanceledException)
        {
            writer.Warning("Turn cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            writer.Error($"{ex.GetType().Name}: {ex.Message}");
            if (verbose)
            {
                AnsiConsole.MarkupLine($"[grey]{ex.ToString().EscapeMarkup()}[/]");
                WriteDiagnostics(workspaceRoot, ex);
            }

            writer.Info($"Details: turn transcript in {logPath}" + (verbose ? ", full stack trace in diagnostics.log" : string.Empty) + ".");
            return 1;
        }
    }

    /// <summary>
    /// Appends the full exception (with stack trace) to .agent/logs/diagnostics.log so a
    /// failed turn is diagnosable after the fact.
    /// </summary>
    private static void WriteDiagnostics(string workspaceRoot, Exception exception)
    {
        try
        {
            var directory = System.IO.Path.Combine(workspaceRoot, ".agent", "logs");
            System.IO.Directory.CreateDirectory(directory);
            var line = $"{DateTime.UtcNow:O} {exception.GetType().FullName}: {exception}{Environment.NewLine}";
            System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "diagnostics.log"), line);
        }
        catch
        {
            // Diagnostics are best-effort.
        }
    }
}
