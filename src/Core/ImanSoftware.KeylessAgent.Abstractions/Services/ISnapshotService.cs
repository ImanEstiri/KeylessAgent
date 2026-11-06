using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Runs the two workspace bat files and turns their outputs into filtered, chunked attachments.
/// The bat files themselves are never modified; the files on disk are never filtered —
/// filtering applies to the attached copy only.
/// </summary>
public interface ISnapshotService : IService
{
    /// <summary>
    /// Runs <c>tree-view.bat</c> and <c>code-dump.bat</c> without a console window, with stdin closed
    /// and a hard timeout, so the trailing <c>pause</c> can never hang the agent.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>Success, or a <c>KA_SNAPSHOT_*</c> failure naming the missing bat or output file.</returns>
    Task<Outcome> RefreshAsync(string workspaceRoot);

    /// <summary>
    /// Reads the current outputs and builds the attachments for the next message,
    /// applying <c>Snapshot.Filter</c> and splitting oversized dumps at file boundaries.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="options">The effective configuration.</param>
    /// <returns>One tree attachment plus one or more code-dump parts.</returns>
    /// <summary>
    /// Builds the SINGLE workspace-context attachment for one outgoing message
    /// (workspace-context.md, kind WorkspaceContext). <paramref name="requestText"/> drives
    /// the size-budget selection (referenced files keep full content, everything else
    /// becomes a per-folder omission note). <paramref name="logEvent"/> receives the
    /// context-budget-exceeded event. Returns an EMPTY list for ContextMode.None and for a
    /// changed mode with no changes since the last build.
    /// </summary>
    Task<Outcome<IReadOnlyList<SnapshotAttachment>>> BuildAttachmentsAsync(
        string workspaceRoot, KeylessOptions options,
        ContextMode mode, string? requestText = null,
        IReadOnlyList<string>? explicitPaths = null,
        Func<string, object, Task>? logEvent = null);

    /// <summary>
    /// Whether the outputs are missing or older than the newest workspace source file
    /// (a refresh should run before the first request of a session).
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>True when a refresh is needed.</returns>
    Task<Outcome<bool>> IsStaleAsync(string workspaceRoot);
}
