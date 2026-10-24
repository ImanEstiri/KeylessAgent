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

    /// <inheritdoc />
    public async Task<Outcome> BackupAsync(string workspaceRoot, string fullPath)
    {
        if (!_storage.FileExists(fullPath))
        {
            return Outcome.Failure(new OutcomeError(
                $"File not found: {fullPath}", ErrorCodes.FileNotFound, OutcomeErrorType.NotFound));
        }

        var relative = Path.GetRelativePath(workspaceRoot, fullPath);
        var stamp = _time.Now.ToUniversalTime().ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var backupRoot = Path.Combine(workspaceRoot, BackupFolderName, stamp);
        var backupPath = Path.Combine(backupRoot, relative);

        var createOutcome = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(backupPath)!);
        if (createOutcome.IsFailure)
            return createOutcome;

        var copyOutcome = await _storage.CopyAsync(fullPath, backupPath);
        if (copyOutcome.IsFailure)
            return Outcome.Failure(new OutcomeError(
                $"Backup failed for {relative}: {copyOutcome.Error.Message}", ErrorCodes.FileIoFailure, OutcomeErrorType.Failure));

        return Outcome.Successful();
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
        foreach (var file in files.Data)
        {
            var relative = Path.GetRelativePath(newest, file.FullPath);
            var target = Path.Combine(workspaceRoot, relative);

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
