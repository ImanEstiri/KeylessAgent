namespace ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

/// <summary>
/// What the ONE workspace-context attachment contains for a message.
/// </summary>
public enum ContextMode
{
    /// <summary>The full tree plus a budgeted dump of the workspace text files.</summary>
    [System.ComponentModel.Description("snapshot")]
    Snapshot,

    /// <summary>Files changed since the last context build, with their current content.</summary>
    [System.ComponentModel.Description("changed")]
    Changed,

    /// <summary>Explicitly requested paths only (via @@context files).</summary>
    [System.ComponentModel.Description("files")]
    Files,

    /// <summary>No context attachment.</summary>
    [System.ComponentModel.Description("none")]
    None,
}
