namespace ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

/// <summary>
/// The execution status of a single AgentScript command, reported back to the model.
/// </summary>
public enum CommandStatus
{
    /// <summary>The command executed successfully.</summary>
    Ok,

    /// <summary>The command failed, was blocked by policy, or was declined by the user.</summary>
    Error,

    /// <summary>The command was not executed because the per-response batch limit was reached.</summary>
    Skipped,
}
