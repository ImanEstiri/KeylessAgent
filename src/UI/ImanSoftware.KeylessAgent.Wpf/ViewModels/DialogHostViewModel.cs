using ImanSoftware.KeylessAgent.Wpf.Mvvm;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// Holds the currently visible dialog view model for the shell overlay. The overlay is visible
/// while <see cref="Current"/> is not null; a dialog view model renders through its DataTemplate.
/// </summary>
public sealed class DialogHostViewModel : ObservableObject
{
    private object? _current;

    /// <summary>The view model of the dialog that is currently shown; null when none is open.</summary>
    public object? Current
    {
        get => _current;

        private set
        {
            if (SetProperty(ref _current, value))
                OnPropertyChanged(nameof(HasCurrent));
        }
    }

    /// <summary>Whether a dialog is currently shown (drives the overlay visibility).</summary>
    public bool HasCurrent => _current is not null;

    /// <summary>Shows a dialog view model, replacing whatever was visible.</summary>
    /// <param name="dialog">The dialog view model; must not be null.</param>
    public void Show(object dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        Current = dialog;
    }

    /// <summary>Hides the current dialog, if any.</summary>
    public void Hide() => Current = null;
}
