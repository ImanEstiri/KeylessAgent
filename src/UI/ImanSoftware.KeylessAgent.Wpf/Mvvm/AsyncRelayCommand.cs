using System.Windows.Input;

namespace ImanSoftware.KeylessAgent.Wpf.Mvvm;

/// <summary>
/// An asynchronous ICommand that disables itself while running, exposes the running state,
/// catches exceptions and forwards them to an error callback — never throwing on the UI
/// thread. If an operation is already running, Execute is ignored.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception> _onError;
    private bool _isRunning;

    /// <summary>Raised when IsRunning flips (for spinners and disabled states).</summary>
    public event EventHandler<IsRunningChangedEventArgs>? IsRunningChanged;

    /// <summary>True while the async operation is in flight.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Creates an async command with an optional can-execute predicate.</summary>
    /// <param name="execute">The asynchronous operation.</param>
    /// <param name="canExecute">Optional predicate re-checked before each execution.</param>
    /// <param name="onError">Receives every exception escaping the operation (never rethrown).</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError ?? (_ => { });
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>Raises CanExecuteChanged so bound UI re-evaluates the command state.</summary>
    public void NotifyCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public bool CanExecute(object? parameter)
        => !_isRunning && (_canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (_isRunning || !CanExecute(parameter))
            return;

        _isRunning = true;
        NotifyCanExecuteChanged();
        IsRunningChanged?.Invoke(this, new IsRunningChangedEventArgs(true));
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
        finally
        {
            _isRunning = false;
            IsRunningChanged?.Invoke(this, new IsRunningChangedEventArgs(false));
            NotifyCanExecuteChanged();
        }
    }

}

/// <summary>Event data for an <see cref="AsyncRelayCommand.IsRunningChanged"/> flip.</summary>
public sealed class IsRunningChangedEventArgs : EventArgs
{
    /// <summary>Initializes the event data.</summary>
    /// <param name="isRunning">Whether the command started running.</param>
    public IsRunningChangedEventArgs(bool isRunning) => IsRunning = isRunning;

    /// <summary>Whether the command is running.</summary>
    public bool IsRunning { get; }
}
