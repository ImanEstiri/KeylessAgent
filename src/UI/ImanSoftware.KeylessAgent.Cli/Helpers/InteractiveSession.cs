using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli.Helpers;

/// <summary>
/// The interactive request loop: prompts for request after request. A failing turn is
/// reported and the loop returns to the prompt — the process exits non-zero only for
/// startup errors, not for a failed turn.
/// </summary>
internal sealed class InteractiveSession(IAnsiConsole console, Func<string, CancellationToken, Task<int>> runTurn)
{
    /// <summary>
    /// Runs the loop until the user submits an empty request.
    /// </summary>
    /// <param name="cancellationToken">Cancels the loop (process shutdown).</param>
    /// <returns>Process exit code.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string request;
            try
            {
                request = await console.PromptAsync(
                    new TextPrompt<string>("[cyan]keyless>[/]").AllowEmpty(),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(request))
                return 0;

            // Ctrl+C cancels only the current turn: a fresh linked token per turn,
            // the outer token (real shutdown) still flows through.
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var exitCode = await runTurn(request.Trim(), turnCts.Token);
                if (exitCode != 0)
                    console.MarkupLine("[grey](turn ended with a problem; the session continues)[/]");
            }
            catch (OperationCanceledException)
            {
                console.MarkupLine("[yellow][[!]][/] Turn cancelled.");
            }
            catch (Exception ex)
            {
                console.MarkupLine($"[red][[x]][/] Turn failed: {ex.Message.EscapeMarkup()}");
                console.MarkupLine("[grey]Details are in .agent/logs (turn transcript; diagnostics.log with -v).[/]");
            }

            console.WriteLine();
        }
    }
}
