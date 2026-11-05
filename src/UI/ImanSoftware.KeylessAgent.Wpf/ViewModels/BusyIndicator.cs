using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// Stack of active wait messages (one per <c>BeginWait</c> scope). The most recently pushed
/// message is displayed; nested scopes show the newest wait. Thread-safe: Core pushes from any
/// thread and the change notification is marshalled to the UI thread.
/// </summary>
public sealed class BusyIndicator : ObservableObject
{
    private readonly IUiDispatcher _ui;
    private readonly object _gate = new();
    private readonly List<WaitEntry> _stack = [];
    private string? _currentMessage;

    /// <summary>Creates the busy indicator.</summary>
    /// <param name="ui">Dispatcher used to raise change notifications on the UI thread.</param>
    public BusyIndicator(IUiDispatcher ui)
    {
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <summary>The message of the innermost active wait; null when nothing is waiting.</summary>
    public string? CurrentMessage
    {
        get => _currentMessage;

        private set
        {
            if (SetProperty(ref _currentMessage, value))
                OnPropertyChanged(nameof(IsBusy));
        }
    }

    /// <summary>Whether at least one wait scope is active.</summary>
    public bool IsBusy => _currentMessage is not null;

    /// <summary>Pushes a wait message and returns the scope to dispose when the wait ends.</summary>
    /// <param name="message">What is being waited for.</param>
    /// <returns>A disposable scope; disposing twice is safe.</returns>
    public IDisposable Push(string message)
    {
        var entry = new WaitEntry(string.IsNullOrWhiteSpace(message) ? "Waiting…" : message);
        string top;
        lock (_gate)
        {
            _stack.Add(entry);
            top = _stack[^1].Message;
        }

        Publish(top);
        return new WaitScope(this, entry);
    }

    /// <summary>Removes a wait entry and republishes the new top of the stack.</summary>
    /// <param name="entry">The entry pushed earlier.</param>
    internal void Pop(WaitEntry entry)
    {
        string? top;
        lock (_gate)
        {
            var index = _stack.IndexOf(entry);
            if (index >= 0)
                _stack.RemoveAt(index);

            top = _stack.Count > 0 ? _stack[^1].Message : null;
        }

        Publish(top);
    }

    private void Publish(string? message) => _ui.Post(() => CurrentMessage = message);

    internal sealed record WaitEntry(string Message);

    private sealed class WaitScope(BusyIndicator owner, WaitEntry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            owner.Pop(entry);
        }
    }
}
