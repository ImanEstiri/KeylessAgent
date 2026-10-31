using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels.Dialogs;

/// <summary>
/// View model of the confirmation dialog: title, detail text in a selectable monospace box,
/// Approve (Enter) and Decline (Esc). The answer callback is invoked exactly once.
/// </summary>
public sealed class ConfirmDialogViewModel : ObservableObject
{
    private readonly ConfirmRequest _request;
    private readonly Action<bool> _answer;

    /// <summary>Creates the confirmation dialog.</summary>
    /// <param name="request">Title and details to display.</param>
    /// <param name="answer">Called once with the user's decision.</param>
    public ConfirmDialogViewModel(ConfirmRequest request, Action<bool> answer)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _answer = answer ?? throw new ArgumentNullException(nameof(answer));
        ApproveCommand = new RelayCommand(() => _answer(true));
        DeclineCommand = new RelayCommand(() => _answer(false));
    }

    /// <summary>The dialog title.</summary>
    public string Title => _request.Title;

    /// <summary>The detail text shown in the monospace box.</summary>
    public string Details => _request.Details;

    /// <summary>Approves the request (gold default button, Enter key).</summary>
    public RelayCommand ApproveCommand { get; }

    /// <summary>Declines the request (ghost button, Esc key).</summary>
    public RelayCommand DeclineCommand { get; }
}
