using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using Spectre.Console;

namespace ImanSoftware.KeylessAgent.Cli;

/// <summary>
/// The CLI implementation of the UI contract. All console access goes through one gate
/// (<see cref="_gate"/>); the wait spinner is a background task started by <see cref="BeginWait"/>,
/// which returns only after the Status display has actually started, so a prompt can never run
/// in the gap between the two. Every other method stops and FULLY joins the spinner (no short
/// timeout) before any prompt or output — a live display and a prompt are never active at the
/// same time, on any console speed.
/// </summary>
internal sealed class ConsoleUserInterface : IAgentUserInterface, IDisposable
{
    /// <summary>
    /// Upper bound for joining a dying spinner. Normally milliseconds; a stuck console gets
    /// a warning after this long instead of an endless block.
    /// </summary>
    private static readonly TimeSpan SpinnerJoinUpperBound = TimeSpan.FromSeconds(30);

    private const string Esc = "";

    private readonly IAnsiConsole _console;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SpinnerRun? _spinner;

    public ConsoleUserInterface(IAnsiConsole? console = null)
    {
        _console = console ?? AnsiConsole.Console;
    }

    /// <summary>
    /// Whether a wait display (spinner) is currently active — for tests.
    /// </summary>
    internal bool IsSpinnerActive => Volatile.Read(ref _spinner) is not null;

    /// <inheritdoc />
    public IDisposable BeginWait(string message)
    {
        SpinnerRun run;
        _gate.Wait(CancellationToken.None);
        try
        {
            // Overlapping or nested BeginWait: the previous spinner is stopped first.
            StopSpinnerLocked();
            run = SpinnerRun.Start(_console, message);
            Volatile.Write(ref _spinner, run);
        }
        finally
        {
            _gate.Release();
        }

        // Do not return until the Status display is really up: otherwise a prompt could start
        // in the gap before the display acquires the console.
        run.Started.Wait(TimeSpan.FromSeconds(10));

        return new WaitScope(this);
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
    public Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default)
    {
        var result = WithGate(() => RunChoiceMenu(request, cancellationToken));
        return Task.FromResult(result);
    }

    private ChoiceResult RunChoiceMenu(ChoiceRequest request, CancellationToken cancellationToken)
    {
        var defaultIndex = FindDefaultIndex(request);

        // Non-interactive input must not block: return the default and tell the user.
        if (!_console.Profile.Capabilities.Interactive || Console.IsInputRedirected)
        {
            var chosen = request.Options[defaultIndex];
            _console.MarkupLine($"[grey]Non-interactive input: selecting [{defaultIndex + 1}] {chosen.Label.EscapeMarkup()} (default)[/]");
            return new ChoiceResult(chosen.Id, Cancelled: false);
        }

        var highlight = defaultIndex;
        var renderedLines = 0;
        RenderMenu(request, highlight, ref renderedLines);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = _console.Input.ReadKey(true);
            if (key is null)
                return new ChoiceResult(request.Options[highlight].Id, Cancelled: false);

            if (key.Value.Key is ConsoleKey.UpArrow)
            {
                highlight = (highlight + request.Options.Count - 1) % request.Options.Count;
                RedrawMenu(request, highlight, renderedLines);
                continue;
            }

            if (key.Value.Key is ConsoleKey.DownArrow)
            {
                highlight = (highlight + 1) % request.Options.Count;
                RedrawMenu(request, highlight, renderedLines);
                continue;
            }

            if (key.Value.Key is ConsoleKey.Enter)
                return new ChoiceResult(request.Options[highlight].Id, Cancelled: false);

            if (key.Value.Key is ConsoleKey.Escape ||
                (key.Value.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Value.Key is ConsoleKey.C))
                return ChoiceResult.CancelledMenu();

            if (key.Value.KeyChar is >= '1' and <= '9')
            {
                var index = key.Value.KeyChar - '1';
                if (index < request.Options.Count)
                    return new ChoiceResult(request.Options[index].Id, Cancelled: false);
            }
        }
    }

    private static int FindDefaultIndex(ChoiceRequest request)
    {
        for (var i = 0; i < request.Options.Count; i++)
        {
            if (request.Options[i].Id == request.DefaultId)
                return i;
        }

        return 0;
    }

    private void RenderMenu(ChoiceRequest request, int highlight, ref int renderedLines)
    {
        _console.MarkupLine($"[cyan]?[/] [white]{request.Title.EscapeMarkup()}[/]");
        WriteMenuBody(request, highlight);
        renderedLines = request.Options.Count + 2;
    }

    private void RedrawMenu(ChoiceRequest request, int highlight, int renderedLines)
    {
        // Move the cursor to the first menu line and overwrite every line (raw ANSI).
        _console.Write($"{Esc}[{renderedLines}A");
        _console.MarkupLine($"[cyan]?[/] [white]{request.Title.EscapeMarkup()}[/]");
        for (var i = 0; i < request.Options.Count; i++)
        {
            _console.Write($"{Esc}[2K");
            WriteOptionLine(request, i, highlight);
        }

        _console.Write($"{Esc}[2K");
        _console.Markup("[grey]↑/↓ move · 1-9 pick · Enter confirm · Esc cancel[/]");
        _console.Write($"{Esc}[0K\n");
    }

    private void WriteMenuBody(ChoiceRequest request, int highlight)
    {
        for (var i = 0; i < request.Options.Count; i++)
            WriteOptionLine(request, i, highlight);
        _console.MarkupLine("[grey]↑/↓ move · 1-9 pick · Enter confirm · Esc cancel[/]");
    }

    private void WriteOptionLine(ChoiceRequest request, int index, int highlight)
    {
        var option = request.Options[index];
        var marker = index == highlight ? "[cyan]>[/]" : " ";
        var isDefault = option.Id == request.DefaultId ? " [grey](default)[/]" : string.Empty;
        var description = option.Description is null ? string.Empty : $" [grey]— {option.Description.EscapeMarkup()}[/]";
        _console.MarkupLine($"{marker} [white]{index + 1}[/]) {option.Label.EscapeMarkup()}{isDefault}{description}");
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
                var oldLines = (change.OldContent ?? string.Empty).Replace("\r\n", "\n").Split('\n');
                var newLines = (change.NewContent ?? string.Empty).Replace("\r\n", "\n").Split('\n');
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

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Wait(CancellationToken.None);
        try
        {
            StopSpinnerLocked();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    /// <summary>
    /// Stops and FULLY joins the active spinner. The caller must hold <see cref="_gate"/>.
    /// </summary>
    private void StopSpinnerLocked()
    {
        var run = Interlocked.Exchange(ref _spinner, null);
        if (run is null)
            return;

        run.Cancellation.Cancel();

        var finished = Task.WaitAny([run.Task], SpinnerJoinUpperBound);
        if (finished != 0)
        {
            // Practically unreachable; a warning beats an unresponsive console.
            _console.MarkupLine("[yellow][[!]][/] The wait display took unusually long to stop; continuing.");
        }

        run.Cancellation.Dispose();
    }

    private void WithGate(Action action)
    {
        _gate.Wait(CancellationToken.None);
        try
        {
            StopSpinnerLocked();
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
            StopSpinnerLocked();
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One running spinner: its task, cancel source and start signal.</summary>
    private sealed class SpinnerRun
    {
        private SpinnerRun(string message, CancellationTokenSource cancellation, ManualResetEventSlim started, Task task)
        {
            Message = message;
            Cancellation = cancellation;
            Started = started;
            Task = task;
        }

        public string Message { get; }

        public CancellationTokenSource Cancellation { get; }

        public ManualResetEventSlim Started { get; }

        public Task Task { get; }

        /// <summary>
        /// Starts the spinner task. <see cref="Started"/> is set from inside the running
        /// Status display, so callers can wait for the display to be really up.
        /// </summary>
        public static SpinnerRun Start(IAnsiConsole console, string message)
        {
            var cancellation = new CancellationTokenSource();
            var started = new ManualResetEventSlim(false);
            var task = Task.Run(() => SpinAsync(console, message, started, cancellation.Token));
            return new SpinnerRun(message, cancellation, started, task);
        }

        private static async Task SpinAsync(
            IAnsiConsole console,
            string message,
            ManualResetEventSlim started,
            CancellationToken cancellationToken)
        {
            try
            {
                await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(message, async _ =>
                    {
                        // The display is up: whoever waits on Started may proceed.
                        started.Set();
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    });
            }
            catch (OperationCanceledException)
            {
                // Normal stop.
            }
            catch (Exception ex)
            {
                // A dying spinner must never kill an agent turn (legacy consoles, redirect pipes).
                console.MarkupLineInterpolated($"[grey][[i]] spinner unavailable ({ex.Message.EscapeMarkup()})[/]");
            }
            finally
            {
                started.Set();
            }
        }
    }

    private sealed class WaitScope(ConsoleUserInterface owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            owner._gate.Wait(CancellationToken.None);
            try
            {
                owner.StopSpinnerLocked();
            }
            finally
            {
                owner._gate.Release();
            }
        }
    }
}
