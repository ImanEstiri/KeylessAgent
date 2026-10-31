using System.Windows.Input;

namespace ImanSoftware.KeylessAgent.Wpf.Mvvm;

/// <summary>
/// A synchronous ICommand that forwards to a delegate. CanExecute can be re-evaluated
/// explicitly by ViewModels (via CommandManager or direct NotifyCanExecuteChanged).
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>Creates a command that always can execute.</summary>
    public RelayCommand(Action execute)
        : this(execute, canExecute: null)
    {
    }

    /// <summary>Creates a command with an optional can-execute predicate.</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>Raises CanExecuteChanged so bound UI re-evaluates the command state.</summary>
    public void NotifyCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute();
}
