using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// One changed path of a change set.
/// </summary>
/// <param name="Path">Workspace-relative path using forward slashes.</param>
/// <param name="Kind">added / modified / deleted / renamed.</param>
public sealed record ChangedPath(string Path, string Kind);

/// <summary>
/// Tracks which workspace paths changed, with git (porcelain before/after a turn) and without
/// git (a lightweight index of path/size/mtime excluding the configured ignore patterns).
/// Powers the dirty-tree menu decision and the turn summary.
/// </summary>
public interface IChangeTracker : IService
{
    /// <summary>
    /// Captures the current change baseline of the workspace.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="gitAvailable">Whether git porcelain can be used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A handle to compare against later.</returns>
    Task<Outcome<object>> CaptureAsync(string workspaceRoot, bool gitAvailable, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares the workspace against a captured baseline.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="baseline">The handle returned by <see cref="CaptureAsync"/>.</param>
    /// <param name="gitAvailable">Whether git porcelain can be used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The changed paths (added / modified / deleted / renamed), or a failure.</returns>
    Task<Outcome<IReadOnlyList<ChangedPath>>> DiffAsync(
        string workspaceRoot,
        object baseline,
        bool gitAvailable,
        CancellationToken cancellationToken = default);
}
