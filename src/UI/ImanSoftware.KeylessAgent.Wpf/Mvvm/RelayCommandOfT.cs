using System.Windows.Input;

namespace ImanSoftware.KeylessAgent.Wpf.Mvvm;

/// <summary>
/// A command with a typed parameter. The parameter arrives as <c>object</c> from WPF binding
/// and is cast; null is passed through as null.
/// </summary>
/// <typeparam name="T">The parameter type.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    /// <summary>
    /// Creates the command.
    /// </summary>
    /// <param name="execute">The action to run; must not be null.</param>
    /// <param name="canExecute">Optional gate evaluated on every <see cref="CanExecute"/> call.</param>
    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <summary>Raises <see cref="CanExecuteChanged"/> so bindings re-query the gate.</summary>
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(Cast(parameter)) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(Cast(parameter));

    private static T? Cast(object? parameter) => parameter is T typed ? typed : default;
}
