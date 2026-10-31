namespace ImanSoftware.KeylessAgent.Abstractions.Models.Agent;

/// <summary>
/// The kind of change a file command applies, for diff display.
/// </summary>
public enum FileChangeKind
{
    /// <summary>A new file is created.</summary>
    Create,

    /// <summary>The whole file content is replaced.</summary>
    Update,

    /// <summary>A single occurrence of text is replaced.</summary>
    Edit,

    /// <summary>The file is deleted.</summary>
    Delete,
}

/// <summary>
/// A pending file change, shown to the user (as a colored diff in Ask mode) before it is applied.
/// </summary>
/// <param name="Path">Workspace-relative path.</param>
/// <param name="Kind">The kind of change.</param>
/// <param name="OldContent">Current content, when the file exists.</param>
/// <param name="NewContent">Content after the change, when the file survives it.</param>
public sealed record FileChange(string Path, FileChangeKind Kind, string? OldContent, string? NewContent);
