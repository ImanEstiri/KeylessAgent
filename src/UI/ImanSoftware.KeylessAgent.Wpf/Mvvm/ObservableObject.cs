using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ImanSoftware.KeylessAgent.Wpf.Mvvm;

/// <summary>
/// Minimal INotifyPropertyChanged base for ViewModels: raises change notifications on the
/// calling thread (WPF marshals binding updates itself) and supports caller-name inference.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for the named property.</summary>
    /// <param name="propertyName">Defaults to the calling member's name.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Compares old and new values and, when different, sets the field and raises
    /// <see cref="PropertyChanged"/>. Returns true when the value changed.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, newValue))
            return false;

        field = newValue;
        OnPropertyChanged(propertyName);
        return true;
    }
}
