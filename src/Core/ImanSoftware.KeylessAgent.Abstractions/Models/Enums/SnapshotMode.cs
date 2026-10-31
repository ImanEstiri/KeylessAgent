namespace ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

/// <summary>
/// What the agent attaches to a user request.
/// </summary>
public enum SnapshotMode
{
    /// <summary>Attach the full tree view and the full code dump (default).</summary>
    Full,

    /// <summary>Attach the full tree view plus only files changed since the last snapshot.</summary>
    TreeAndChanged,
}
