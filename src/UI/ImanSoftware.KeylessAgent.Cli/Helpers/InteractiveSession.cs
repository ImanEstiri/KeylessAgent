using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli.Helpers;

/// <summary>
/// The interactive request loop: prompts for request after request, understands the slash
/// commands (<c>/workspace</c>, <c>/help</c>), and reports failed turns instead of crashing.
/// Core always receives the workspace explicitly — the loop never relies on the current directory.
/// </summary>
internal sealed class InteractiveSession
{
    private readonly IAnsiConsole _console;
    private readonly Func<string, string, CancellationToken, Task<int>> _runTurn;
    private readonly Func<string, string, string?> _switchWorkspace;

    /// <summary>
    /// Creates the loop.
    /// </summary>
    /// <param name="console">The console surface for every prompt and output.</param>
    /// <param name="runTurn">
    /// Runs one turn: (workspace, request, cancellation) → exit code. Called for user requests only.
    /// </param>
    /// <param name="switchWorkspace">
    /// Validates and re-binds to a new workspace (git preconditions + fresh session happen in the
    /// command layer); (oldWorkspace, newWorkspace) → error message, or null on success.
    /// </param>
    public InteractiveSession(
        IAnsiConsole console,
        Func<string, string, CancellationToken, Task<int>> runTurn,
        Func<string, string, string?> switchWorkspace)
    {
        _console = console;
        _runTurn = runTurn;
        _switchWorkspace = switchWorkspace;
    }

    /// <summary>
    /// Runs the loop until the user submits an empty request.
    /// </summary>
    /// <param name="workspace">The current workspace path.</param>
    /// <param name="cancellationToken">Cancels the loop (process shutdown).</param>
    /// <returns>Process exit code and the workspace the session ended on.</returns>
    public async Task<(int ExitCode, string Workspace)> RunAsync(string workspace, CancellationToken cancellationToken)
    {
        while (true)
        {
            string request;
            try
            {
                request = await _console.PromptAsync(
                    new TextPrompt<string>("[cyan]keyless>[/]").AllowEmpty(),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return (0, workspace);
            }

            if (string.IsNullOrWhiteSpace(request))
                return (0, workspace);

            var input = request.Trim();

            if (input.StartsWith('/'))
            {
                var switched = await HandleSlashCommandAsync(input, workspace);
                if (switched is not null)
                    workspace = switched;
                continue;
            }

            // Ctrl+C cancels only the current turn: a fresh linked token per turn,
            // the outer token (real shutdown) still flows through.
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var exitCode = await _runTurn(workspace, input, turnCts.Token);
                if (exitCode != 0)
                    _console.MarkupLine("[grey](turn ended with a problem; the session continues)[/]");
            }
            catch (OperationCanceledException)
            {
                _console.MarkupLine("[yellow][[!]][/] Turn cancelled.");
            }
            catch (Exception ex)
            {
                _console.MarkupLine($"[red][[x]][/] Turn failed: {ex.GetType().Name}: {ex.Message.EscapeMarkup()}");
                _console.MarkupLine("[grey]Details are in .agent/logs (turn transcript; diagnostics.log with -v).[/]");
            }

            _console.WriteLine();
        }
    }

    /// <summary>
    /// Executes a slash command. Returns the new workspace when /workspace switched,
    /// or null when the loop should continue on the current workspace.
    /// </summary>
    private async Task<string?> HandleSlashCommandAsync(string input, string workspace)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "/help":
                PrintHelp();
                return null;

            case "/workspace":
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
                {
                    _console.MarkupLine($"[yellow][[!]][/] Usage: /workspace <path> (current: {workspace.EscapeMarkup()})");
                    return null;
                }

                var newPath = Path.GetFullPath(parts[1].Trim());
                if (!Directory.Exists(newPath))
                {
                    _console.MarkupLine($"[red][[x]][/] Directory not found: {newPath.EscapeMarkup()}");
                    return null;
                }

                var error = _switchWorkspace(workspace, newPath);
                if (error is not null)
                {
                    _console.MarkupLine($"[red][[x]][/] {error.EscapeMarkup()}");
                    return null;
                }

                ConsoleWriter.Shared.Header(newPath);
                _console.MarkupLine("[grey]New chat session started for this workspace (its own .agent/session.json).[/]");
                return newPath;

            default:
                _console.MarkupLine($"[red][[x]][/] Unknown command \"{input.EscapeMarkup()}\". Available: /workspace <path>, /help. Plain requests need no leading '/'.");
                return null;
        }
    }

    private void PrintHelp()
    {
        _console.MarkupLine("[cyan]Available commands[/]");
        _console.MarkupLine("  [white]/workspace <path>[/]  Switch to another workspace (validated; fresh chat session)");
        _console.MarkupLine("  [white]/help[/]              Show this list");
        _console.MarkupLine("[grey]Anything else is sent to the agent as a request. An empty line quits.[/]");
    }
}
