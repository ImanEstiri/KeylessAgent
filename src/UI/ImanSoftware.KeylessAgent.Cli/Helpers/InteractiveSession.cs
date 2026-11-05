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
    private readonly Func<string, string, CancellationToken, Task<string?>> _switchWorkspace;
    private readonly ImanSoftware.KeylessAgent.Core.Implementation.Workspace.IEnvironmentTools? _environmentTools;
    private bool _toolsLinePrinted;

    /// <summary>Set when a graceful exit was requested (idle Ctrl+C or the second press).</summary>
    private readonly TaskCompletionSource<bool> _exitRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource? _turnCts;
    private DateTime _lastCancelPress = DateTime.MinValue;

    /// <summary>
    /// Creates the loop.
    /// </summary>
    /// <param name="console">The console surface for every prompt and output.</param>
    /// <param name="runTurn">
    /// Runs one turn: (workspace, request, cancellation) → exit code. Called for user requests only.
    /// </param>
    /// <param name="switchWorkspace">
    /// Validates and re-binds to a new workspace (git preconditions + fresh session happen in the
    /// command layer); (oldWorkspace, newWorkspace, cancellation) → error message, or null on success.
    /// </param>
    public InteractiveSession(
        IAnsiConsole console,
        Func<string, string, CancellationToken, Task<int>> runTurn,
        Func<string, string, CancellationToken, Task<string?>> switchWorkspace,
        ImanSoftware.KeylessAgent.Core.Implementation.Workspace.IEnvironmentTools? environmentTools = null)
    {
        _console = console;
        _runTurn = runTurn;
        _switchWorkspace = switchWorkspace;
        _environmentTools = environmentTools;
    }

    /// <summary>
    /// The single Ctrl+C entry point, wired to <see cref="Console.CancelKeyPress"/> while the
    /// session runs (and callable directly in tests): during a turn the first press cancels only
    /// the turn; a second press within 2 seconds — or a press at the idle prompt — exits the
    /// session gracefully (logs are flushed per turn, the connector is disposed per turn).
    /// </summary>
    internal void RequestCancel()
    {
        var now = DateTime.UtcNow;
        var isSecondPress = (now - _lastCancelPress).TotalSeconds <= 2;
        _lastCancelPress = now;

        if (_turnCts is { IsCancellationRequested: false } turn)
        {
            turn.Cancel();
            if (isSecondPress)
                _exitRequested.TrySetResult(true);
            return;
        }

        _exitRequested.TrySetResult(true);
    }

    /// <summary>
    /// Runs the loop until the user submits an empty request or Ctrl+C exits gracefully.
    /// </summary>
    /// <param name="workspace">The current workspace path.</param>
    /// <param name="cancellationToken">Cancels the loop (process shutdown).</param>
    /// <returns>Process exit code and the workspace the session ended on.</returns>
    public async Task<(int ExitCode, string Workspace)> RunAsync(string workspace, CancellationToken cancellationToken)
    {
        Console.CancelKeyPress += OnCancelKeyPress;

        // One explicit tool status per session (git/dotnet/python/node with download pages).
        if (_environmentTools is not null && !_toolsLinePrinted)
        {
            _toolsLinePrinted = true;
            try
            {
                var tools = await _environmentTools.GetToolsCachedAsync(workspace, forceRefresh: false, cancellationToken);
                _console.WriteLine(ImanSoftware.KeylessAgent.Core.Implementation.Workspace.IEnvironmentTools.FormatToolsLine(tools));
            }
            catch
            {
                // The tools line is informational; a probe failure never blocks the session.
            }
        }

        try
        {
            while (true)
            {
                string request;
                try
                {
                    var promptTask = _console.PromptAsync(
                        new TextPrompt<string>("[cyan]keyless>[/]").AllowEmpty(),
                        cancellationToken);
                    var completed = await Task.WhenAny(promptTask, _exitRequested.Task);
                    if (completed != promptTask)
                        return (0, workspace); // Ctrl+C at the idle prompt: graceful exit.

                    request = await promptTask; // propagates prompt errors (incl. outer cancel).
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
                    var switched = await HandleSlashCommandAsync(input, workspace, cancellationToken);
                    if (switched is not null)
                        workspace = switched;
                    if (_exitRequested.Task.IsCompleted)
                        return (0, workspace);
                    continue;
                }

                // Ctrl+C cancels only the current turn: a fresh linked token per turn,
                // the outer token (real shutdown) still flows through.
                using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _turnCts = turnCts;
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
                finally
                {
                    _turnCts = null;
                }

                if (_exitRequested.Task.IsCompleted)
                    return (0, workspace); // second Ctrl+C during the turn: graceful exit.

                _console.WriteLine();
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Take over the signal: the session decides between turn-cancel and graceful exit.
        e.Cancel = true;
        RequestCancel();
    }

    /// <summary>
    /// Executes a slash command. Returns the new workspace when /workspace switched,
    /// or null when the loop should continue on the current workspace.
    /// </summary>
    private async Task<string?> HandleSlashCommandAsync(string input, string workspace, CancellationToken cancellationToken)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "/help":
                PrintHelp();
                return null;

            case "/tools":
            case "/refreshenv":
                if (_environmentTools is null)
                {
                    _console.MarkupLine("[yellow][[!]][/] Environment tools are not available in this session.");
                    return null;
                }

                var refreshed = await _environmentTools.GetToolsCachedAsync(
                    workspace, forceRefresh: command == "/refreshenv", cancellationToken);
                _console.WriteLine(ImanSoftware.KeylessAgent.Core.Implementation.Workspace.IEnvironmentTools.FormatToolsLine(refreshed));
                if (command == "/refreshenv")
                    _console.MarkupLine("[cyan]Environment re-probed.[/]");
                return null;

            case "/newchat":
                var clearError = await _switchWorkspace(workspace, workspace, cancellationToken);
                if (clearError is not null)
                {
                    _console.MarkupLine($"[red][[x]][/] {clearError.EscapeMarkup()}");
                    return null;
                }
                _console.MarkupLine("[cyan]New chat session started (same workspace).[/]");
                return workspace;

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

                var error = await _switchWorkspace(workspace, newPath, cancellationToken);
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
        _console.MarkupLine("  [white]/newchat[/]           Start a fresh chat in the same workspace");
        _console.MarkupLine("  [white]/tools[/]            Show the installed tools (git/dotnet/python/node)");
        _console.MarkupLine("  [white]/refreshenv[/]       Re-probe the environment tools");
        _console.MarkupLine("  [white]/help[/]              Show this list");
        _console.MarkupLine("[grey]Anything else is sent to the agent as a request. An empty line quits.[/]");
    }
}
