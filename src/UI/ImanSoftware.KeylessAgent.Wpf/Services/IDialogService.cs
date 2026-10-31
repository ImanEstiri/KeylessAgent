using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>
/// Shows modal dialogs inside the shell overlay. One dialog at a time — further requests queue
/// until the current one is answered. <see cref="CancelAll"/> completes every pending request
/// (confirm → false, choice → cancelled, ask → empty) so a stopped turn cannot hang the agent.
/// </summary>
public interface IDialogService
{
    /// <summary>Shows a yes/no dialog and completes with the user's decision.</summary>
    /// <param name="request">Title and detail text to display.</param>
    /// <returns>True when the user approved.</returns>
    Task<bool> ConfirmAsync(ConfirmRequest request);

    /// <summary>Shows a multiline text-input dialog and completes with the answer.</summary>
    /// <param name="question">The question shown above the input box.</param>
    /// <returns>The user's answer; empty when cancelled.</returns>
    Task<string> AskAsync(string question);

    /// <summary>Shows a numbered choice menu and completes with the selection.</summary>
    /// <param name="request">Title, options and default option.</param>
    /// <param name="cancellationToken">Completes the menu as cancelled when triggered.</param>
    /// <returns>The selection result.</returns>
    Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Completes every pending dialog as if the user cancelled it.</summary>
    void CancelAll();
}
