namespace ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;

/// <summary>
/// What kind of snapshot content an attachment carries.
/// </summary>
public enum SnapshotAttachmentKind
{
    /// <summary>The filtered <c>tree-view-result.txt</c> output.</summary>
    TreeView,

    /// <summary>The filtered <c>code-dump-result.md</c> output, or one part of it.</summary>
    CodeDump,

    /// <summary>The list of files changed since the last snapshot (legacy TreeAndChanged mode).</summary>
    ChangedFiles,

    /// <summary>The SINGLE workspace-context attachment (one per message, all modes).</summary>
    WorkspaceContext,
}

/// <summary>
/// One piece of workspace state attached to a message sent to the chat.
/// </summary>
/// <param name="Name">Attachment file name shown in the chat (e.g. <c>code-dump-result-part1.md</c>).</param>
/// <param name="Kind">Which snapshot this is.</param>
/// <param name="Content">The filtered text to upload or paste.</param>
public sealed record SnapshotAttachment(string Name, SnapshotAttachmentKind Kind, string Content);
