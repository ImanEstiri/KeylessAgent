using ImanSoftware.KeylessAgent.Wpf.Mvvm;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels.Dialogs;

/// <summary>
/// View model of the crash/error dialog: a title, the short message and a single OK button.
/// Shown by the global exception handler through the dialog host.
/// </summary>
public sealed class ErrorDialogViewModel : ObservableObject
{
    /// <summary>Creates the error dialog.</summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The short error message.</param>
    /// <param name="close">Closes the dialog (invoked by OK).</param>
    public ErrorDialogViewModel(string title, string message, Action close)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        OkCommand = new RelayCommand(close);
    }

    /// <summary>The dialog title.</summary>
    public string Title { get; }

    /// <summary>The short error message.</summary>
    public string Message { get; }

    /// <summary>Closes the dialog (OK button, Enter/Esc).</summary>
    public RelayCommand OkCommand { get; }
}
