using ImanSoftware.DateTimeProvider;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Backs up files into <c>.agent/backups/&lt;timestamp&gt;/…</c> before any change and restores
/// the newest batch for <c>keyless undo</c>.
/// </summary>
internal sealed class BackupService : IBackupService
{
    private const string BackupFolderName = ".agent\\backups";
    private const string JournalFileName = "journal.jsonl";

    private readonly IFileStorage _storage;
    private readonly IDateTimeProvider _time;

    public BackupService()
        : this(ImanFileStorageFactory.CreateStorage(), new DefaultDateTimeProvider())
    {
    }

    internal BackupService(IFileStorage storage, IDateTimeProvider time)
    {
        _storage = storage;
        _time = time;
    }

    /// <summary>One journal line: what the command did besides modifying a backed-up file.</summary>
    internal sealed record JournalEntry(string Op, string? Path = null, string? From = null, string? To = null);

    /// <inheritdoc />
    public async Task<Outcome> BackupAsync(string workspaceRoot, string fullPath)
    {
        var batch = await BackupToBatchAsync(workspaceRoot, fullPath);
        return batch.IsFailure ? Outcome.Failure(batch.Error) : Outcome.Successful();
    }

    /// <summary>
    /// Backs a file up into a fresh timestamped batch and returns the batch stamp so the
    /// caller can journal extra undo information (e.g. a move destination) into the same batch.
    /// </summary>
    internal async Task<Outcome<string>> BackupToBatchAsync(string workspaceRoot, string fullPath)
    {
        if (!_storage.FileExists(fullPath))
        {
            return Outcome.Failure<string>(new OutcomeError(
                $"File not found: {fullPath}", ErrorCodes.FileNotFound, OutcomeErrorType.NotFound));
        }

        var relative = Path.GetRelativePath(workspaceRoot, fullPath);

        // The sandbox refuses symlink/junction escapes, .git/.agent, streams and traversal —
        // a source that is a link pointing outside the workspace is never backed up.
        var sandbox = new PathSandbox(workspaceRoot);
        var resolved = sandbox.Resolve(relative.Replace('\\', '/'));
        if (resolved.IsFailure)
            return Outcome.Failure<string>(new OutcomeError(
                $"Backup refused: {resolved.Error.Message}", ErrorCodes.PathOutsideWorkspace, OutcomeErrorType.Validation));

        var stamp = _time.Now.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var backupRoot = Path.Combine(workspaceRoot, BackupFolderName, stamp);
        var backupPath = Path.Combine(backupRoot, relative);

        var createOutcome = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(backupPath)!);
        if (createOutcome.IsFailure)
            return Outcome.Failure<string>(createOutcome.Error);

        var copyOutcome = await _storage.CopyAsync(fullPath, backupPath);
        if (copyOutcome.IsFailure)
            return Outcome.Failure<string>(new OutcomeError(
                $"Backup failed for {relative}: {copyOutcome.Error.Message}", ErrorCodes.FileIoFailure, OutcomeErrorType.Failure));

        return Outcome.Successful(stamp);
    }

    /// <summary>
    /// Journals a command side effect for <c>keyless undo</c> into an existing batch
    /// (best-effort: a failed journal line only loses undo precision, never fails the command).
    /// </summary>
    internal async Task<Outcome> AppendJournalAsync(string workspaceRoot, string stamp, JournalEntry entry)
    {
        var journalPath = Path.Combine(workspaceRoot, BackupFolderName, stamp, JournalFileName);
        var mkdir = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(journalPath)!);
        if (mkdir.IsFailure)
            return mkdir;

        var line = System.Text.Json.JsonSerializer.Serialize(entry) + "\n";
        if (_storage.FileExists(journalPath))
        {
            var read = await _storage.ReadAllTextAsync(journalPath);
            if (read.IsSuccess)
                line = read.Data + line;
        }

        return await _storage.WriteAllTextAsync(journalPath, line);
    }

    /// <summary>
    /// Journals a file CREATION into a fresh batch (no file is backed up — undo DELETES the
    /// created file instead of restoring it). Best-effort like every journal write.
    /// </summary>
    internal async Task<Outcome> RecordCreateAsync(string workspaceRoot, string createdFullPath)
    {
        var relative = Path.GetRelativePath(workspaceRoot, createdFullPath);
        var stamp = _time.Now.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var batchDir = Path.Combine(workspaceRoot, BackupFolderName, stamp);
        var mkdir = await _storage.CreateDirectoryAsync(batchDir);
        if (mkdir.IsFailure)
            return mkdir;

        return await AppendJournalAsync(workspaceRoot, stamp, new JournalEntry("create", Path: relative.Replace('\\', '/')));
    }

    /// <inheritdoc />
    public async Task<Outcome<IReadOnlyList<string>>> UndoLatestAsync(string workspaceRoot)
    {
        var backupsRoot = Path.Combine(workspaceRoot, BackupFolderName);
        if (!_storage.DirectoryExists(backupsRoot))
        {
            return Outcome.Failure<IReadOnlyList<string>>(new OutcomeError(
                "No backups exist yet.", ErrorCodes.FileNotFound, OutcomeErrorType.NotFound));
        }

        var stampFolders = await _storage.EnumerateDirectoriesAsync(backupsRoot, "*", SearchOption.TopDirectoryOnly);
        if (stampFolders.IsFailure)
            return Outcome.Failure<IReadOnlyList<string>>(stampFolders.Error);

        var newest = stampFolders.Data
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (newest is null)
        {
            return Outcome.Failure<IReadOnlyList<string>>(new OutcomeError(
                "No backups exist yet.", ErrorCodes.FileNotFound, OutcomeErrorType.NotFound));
        }

        var files = await _storage.SearchFilesAsync(newest, ["*"], SearchOption.AllDirectories);
        if (files.IsFailure)
            return Outcome.Failure<IReadOnlyList<string>>(files.Error);

        var restored = new List<string>();
        var sandbox = new PathSandbox(workspaceRoot);

        // Journal side effects run BEFORE the file restore: a created file is deleted outright
        // and a move's destination is deleted (the source comes back with the copy loop below).
        var journalPath = Path.Combine(newest, JournalFileName);
        if (_storage.FileExists(journalPath))
        {
            var journal = await _storage.ReadAllTextAsync(journalPath);
            if (journal.IsSuccess)
            {
                foreach (var line in journal.Data.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    JournalEntry? entry;
                    try
                    {
                        entry = System.Text.Json.JsonSerializer.Deserialize<JournalEntry>(line);
                    }
                    catch
                    {
                        entry = null;
                    }

                    if (entry is null)
                        continue;

                    var affected = entry.Op == "move" ? entry.To : entry.Path;
                    if (string.IsNullOrWhiteSpace(affected))
                        continue;

                    var target = sandbox.Resolve(affected);
                    if (target.IsFailure || !_storage.FileExists(target.Data))
                        continue;

                    var deleted = await _storage.DeleteAsync(target.Data);
                    if (deleted.IsSuccess)
                        restored.Add($"removed: {affected}");
                }
            }
        }

        foreach (var file in files.Data)
        {
            var relative = Path.GetRelativePath(newest, file.FullPath);
            if (relative.Equals(JournalFileName, StringComparison.OrdinalIgnoreCase))
                continue; // the journal is undo metadata, never restored into the workspace

            // The sandbox refuses traversal, stream spellings, .git/.agent and link escapes —
            // a planted link in the backup tree must not steer the restore.
            var resolved = sandbox.Resolve(relative.Replace('\\', '/'));
            if (resolved.IsFailure)
            {
                restored.Add($"skipped: {relative} ({resolved.Error.Message})");
                continue;
            }

            var target = resolved.Data;

            // Undo must not write through a reparse point: a target swapped for a symlink
            // after the backup would redirect the restore outside the workspace.
            if (File.Exists(target) && File.ResolveLinkTarget(target, returnFinalTarget: false) is not null)
            {
                restored.Add($"skipped: {relative} (the target path is a symlink or junction)");
                continue;
            }

            var createOutcome = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(target)!);
            if (createOutcome.IsFailure)
                return Outcome.Failure<IReadOnlyList<string>>(createOutcome.Error);

            var copyOutcome = await _storage.CopyAsync(file.FullPath, target);
            if (copyOutcome.IsFailure)
                return Outcome.Failure<IReadOnlyList<string>>(new OutcomeError(
                    $"Restore failed for {relative}: {copyOutcome.Error.Message}",
                    ErrorCodes.FileIoFailure, OutcomeErrorType.Failure));

            restored.Add(relative);
        }

        var removeBatch = await _storage.DeleteDirectoryAsync(newest, recursive: true);
        if (removeBatch.IsFailure)
            return Outcome.Failure<IReadOnlyList<string>>(removeBatch.Error);

        return Outcome.Successful((IReadOnlyList<string>)restored);
    }
}
