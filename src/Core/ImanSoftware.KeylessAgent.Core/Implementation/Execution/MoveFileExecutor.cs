using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Moves/renames a file: sandboxed (both paths), backed up, refuses to overwrite an existing
/// destination, creates missing parent folders, covered by keyless undo.
/// </summary>
internal sealed class MoveFileExecutor : ICommandExecutor
{
    private readonly IFileStorage _storage;
    private readonly IBranchGuard _branchGuard;
    private readonly IBackupService _backup;

    public MoveFileExecutor()
        : this(ImanFileStorageFactory.CreateStorage(), new BranchGuard(), new BackupService())
    {
    }

    internal MoveFileExecutor(IFileStorage storage, IBranchGuard branchGuard, IBackupService backup)
    {
        _storage = storage;
        _branchGuard = branchGuard;
        _backup = backup;
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.MoveFile;

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var move = (MoveFileCommand)command;

        var sandbox = new PathSandbox(context.WorkspaceRoot);
        var source = sandbox.Resolve(move.Path);
        if (source.IsFailure)
            return CommandExecutionResult.Fail(command, source.Error.Code, source.Error.Message);

        var destination = sandbox.Resolve(move.ToPath);
        if (destination.IsFailure)
            return CommandExecutionResult.Fail(command, destination.Error.Code, destination.Error.Message);

        // NO-GIT MODE and DRY RUN: no branch protection (no git, or nothing is mutated).
        if (!context.NoGitMode && !context.DryRun)
        {
            var guard = await _branchGuard.EnsureModifiableAsync(context.WorkspaceRoot, context.Options.Git);
            if (guard.IsFailure)
                return CommandExecutionResult.Fail(command, guard.Error.Code, guard.Error.Message);
        }

        if (!_storage.FileExists(source.Data))
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.FileNotFound,
                $"File not found: {move.Path}");
        }

        if (_storage.FileExists(destination.Data))
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.FileAlreadyExists,
                $"Destination already exists: {move.ToPath}. Refusing to overwrite.");
        }

        if (context.DryRun)
            return NewFileExecutor.DryRunResult(command);

        // Moves are confirmed in Ask AND AutoSafe; only Auto skips (same as deletion). The
        // diff is shown together with the confirmation prompt, not on its own.
        if (context.Options.Mode != ApprovalMode.Auto)
        {
            context.Ui.ShowDiff(new FileChange(move.Path, FileChangeKind.Edit, null, null));
            var confirmed = await context.Ui.ConfirmAsync(new ConfirmRequest(
                "Move file",
                $"The model wants to move \"{move.Path}\" to \"{move.ToPath}\". A backup of the source is stored in .agent/backups."));
            if (!confirmed)
                return CommandExecutionResult.Fail(command, ErrorCodes.CommandDeclined, "The user declined this move.");
        }

        var backup = await _backup.BackupAsync(context.WorkspaceRoot, source.Data);
        if (backup.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, backup.Error.Message);

        var directory = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(destination.Data)!, context.CancellationToken);
        if (directory.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, directory.Error.Message);

        var moveOutcome = await _storage.CopyAsync(source.Data, destination.Data);
        if (moveOutcome.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, moveOutcome.Error.Message);

        var delete = await _storage.DeleteAsync(source.Data);
        if (delete.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, delete.Error.Message);

        return new CommandExecutionResult
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = $"Moved {move.Path} -> {move.ToPath}.",
        };
    }
}
