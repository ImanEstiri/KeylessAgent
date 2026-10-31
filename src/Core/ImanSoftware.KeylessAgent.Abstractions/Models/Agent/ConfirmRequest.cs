namespace ImanSoftware.KeylessAgent.Abstractions.Models.Agent;

/// <summary>
/// A yes/no question for the user, shown before a risky command runs.
/// </summary>
/// <param name="Title">Short title of what is about to happen.</param>
/// <param name="Details">Why the confirmation is needed and what exactly will run or change.</param>
public sealed record ConfirmRequest(string Title, string Details);
