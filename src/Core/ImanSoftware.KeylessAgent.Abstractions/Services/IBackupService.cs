using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Backs up files into <c>.agent/backups</c> before any change, powering <c>keyless undo</c>.
/// </summary>
public interface IBackupService : IService
{
    /// <summary>
    /// Copies the current version of a file into a timestamped backup folder.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="fullPath">The absolute path of the file to back up.</param>
    /// <returns>Success or a <c>KA_FILE_IO</c> failure.</returns>
    Task<Outcome> BackupAsync(string workspaceRoot, string fullPath);

    /// <summary>
    /// Restores the newest backup batch: copies every file in the newest timestamped folder
    /// back to its workspace path, then removes that folder.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>The restored file paths, or a <c>KA_FILE_NOT_FOUND</c> failure when no backup exists.</returns>
    Task<Outcome<IReadOnlyList<string>>> UndoLatestAsync(string workspaceRoot);
}
