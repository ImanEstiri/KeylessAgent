namespace ImanSoftware.KeylessAgent.Abstractions.Models.Agent;

/// <summary>
/// One selectable option of a numbered choice menu.
/// </summary>
/// <param name="Id">Stable identifier the agent logic switches on (e.g. "carry", "stash").</param>
/// <param name="Label">Text shown to the user.</param>
/// <param name="Description">Optional longer explanation shown under the label.</param>
public sealed record ChoiceOption(string Id, string Label, string? Description = null);

/// <summary>
/// A numbered choice menu: the user moves with arrow keys (or presses a digit) and confirms
/// with Enter; Esc cancels. UI-agnostic so a Windows Forms UI can implement it with a dialog.
/// </summary>
/// <param name="Title">The question shown above the options.</param>
/// <param name="Options">The options in display order (numbered 1..N).</param>
/// <param name="DefaultId">The initially highlighted option's <see cref="ChoiceOption.Id"/>.</param>
public sealed record ChoiceRequest(
    string Title,
    IReadOnlyList<ChoiceOption> Options,
    string DefaultId);

/// <summary>
/// The outcome of a choice menu.
/// </summary>
/// <param name="SelectedId">The chosen option's id, or null when the menu was cancelled.</param>
/// <param name="Cancelled">Whether the user cancelled (Esc / Ctrl+C).</param>
public sealed record ChoiceResult(string? SelectedId, bool Cancelled)
{
    /// <summary>A cancelled menu result.</summary>
    public static ChoiceResult CancelledMenu() => new(null, true);
}
