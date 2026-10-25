using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli;

/// <summary>
/// The CLI implementation of the UI contract. All console access goes through one gate
/// (<see cref="_gate"/>); the wait spinner is a background task started by <see cref="BeginWait"/>
/// and stopped by every other method (and by disposing the wait scope) before any prompt or
/// output runs — a live display and a prompt are never active at the same time.
/// </summary>
internal sealed class ConsoleUserInterface : IAgentUserInterface, IDisposable
{
    private readonly IAnsiConsole _console;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _spinnerCts;
    private Task? _spinnerTask;

    public ConsoleUserInterface(IAnsiConsole? console = null)
    {
        _console = console ?? AnsiConsole.Console;
    }

    /// <summary>
    /// Whether a wait display (spinner) is currently active — for tests.
    /// </summary>
    internal bool IsSpinnerActive => Volatile.Read(ref _spinnerCts) is not null;

    /// <inheritdoc />
    public IDisposable BeginWait(string message)
    {
        _gate.Wait(CancellationToken.None);
        try
        {
            // Overlapping or nested BeginWait: the previous spinner is stopped first.
            StopSpinner();
            var cts = new CancellationTokenSource();
            var task = Task.Run(() => SpinAsync(message, cts.Token), cts.Token);
            Volatile.Write(ref _spinnerCts, cts);
            Volatile.Write(ref _spinnerTask, task);
            return new WaitScope(this, cts, task);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Say(string message)
    {
        WithGate(() =>
        {
            var panel = new Panel(message.EscapeMarkup())
                .Header("[cyan]DeepSeek says[/]")
                .Border(BoxBorder.Rounded)
                .BorderColor(Color.Cyan1);
            _console.Write(panel);
        });
    }

    /// <inheritdoc />
    public void ShowProgress(AgentProgressEvent progressEvent)
    {
        WithGate(() => _console.MarkupLine(
            $"[grey]{progressEvent.Type}[/] {progressEvent.Detail.EscapeMarkup()}"));
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        var result = WithGate(() =>
        {
            var prompt = new ConfirmationPrompt(
                $"[yellow]?[/] {request.Title.EscapeMarkup()}: {request.Details.EscapeMarkup()}");
            return prompt.Show(_console);
        });
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<string> AskAsync(string question)
    {
        var result = WithGate(() =>
            _console.Prompt(new TextPrompt<string>($"[yellow]?[/] {question.EscapeMarkup()}")));
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public void ShowDiff(FileChange change)
    {
        WithGate(() =>
        {
            var title = change.Kind switch
            {
                FileChangeKind.Create => $"[green]+ new file: {change.Path}[/]",
                FileChangeKind.Update => $"[yellow]~ update file: {change.Path}[/]",
                FileChangeKind.Edit => $"[yellow]~ edit file: {change.Path}[/]",
                _ => $"[red]- delete file: {change.Path}[/]",
            };

            var grid = new Grid().Expand();
            grid.AddColumn();
            grid.AddRow(title);

            if (change.OldContent is not null || change.NewContent is not null)
            {
                var oldLines = (change.OldContent ?? string.Empty).Split('\n');
                var newLines = (change.NewContent ?? string.Empty).Split('\n');
                var max = Math.Max(oldLines.Length, newLines.Length);
                for (var i = 0; i < max && i < 40; i++)
                {
                    var oldLine = i < oldLines.Length ? oldLines[i] : null;
                    var newLine = i < newLines.Length ? newLines[i] : null;
                    if (oldLine == newLine)
                        continue;

                    if (oldLine is not null)
                        grid.AddRow($"[red]- {oldLine.EscapeMarkup()}[/]");
                    if (newLine is not null)
                        grid.AddRow($"[green]+ {newLine.EscapeMarkup()}[/]");
                }

                if (max > 40)
                    grid.AddRow($"[grey]... {max - 40} more lines[/]");
            }

            _console.Write(grid);
        });
    }

    private async Task SpinAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await _console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(message.EscapeMarkup(), async _ =>
                {
                    // The scope's Dispose (or any other UI call) cancels this token.
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                });
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception)
        {
            // A dying spinner must never kill an agent turn (legacy consoles, redirect pipes).
        }
    }

    /// <summary>
    /// Stops and joins the active spinner. The caller must hold <see cref="_gate"/>.
    /// </summary>
    private void StopSpinner()
    {
        var cts = Interlocked.Exchange(ref _spinnerCts, null);
        var task = Interlocked.Exchange(ref _spinnerTask, null);
        if (cts is null)
            return;

        cts.Cancel();
        try
        {
            task?.Wait(TimeSpan.FromSeconds(3), CancellationToken.None);
        }
        catch (AggregateException)
        {
            // The spinner swallows its own failures; ignore join noise.
        }

        cts.Dispose();
    }

    private void WithGate(Action action)
    {
        _gate.Wait(CancellationToken.None);
        try
        {
            StopSpinner();
            action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private T WithGate<T>(Func<T> action)
    {
        _gate.Wait(CancellationToken.None);
        try
        {
            StopSpinner();
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Wait(CancellationToken.None);
        try
        {
            StopSpinner();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private sealed class WaitScope(ConsoleUserInterface owner, CancellationTokenSource cts, Task task) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            owner._gate.Wait(CancellationToken.None);
            try
            {
                owner.StopSpinner();
            }
            finally
            {
                owner._gate.Release();
            }

            // When another UI call (or a newer BeginWait) already stopped this spinner,
            // the cts was cancelled and disposed there — cancelling again must not throw.
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                task.Wait(TimeSpan.FromSeconds(3), CancellationToken.None);
            }
            catch (AggregateException)
            {
                // Join noise from an already-cancelled spinner is irrelevant here.
            }

            cts.Dispose();
        }
    }
}
