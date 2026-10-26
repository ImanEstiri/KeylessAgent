using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Deletes a workspace file after backing it up. In Ask mode the user confirms every deletion.
/// </summary>
internal sealed class DeleteFileExecutor : ICommandExecutor
{
    private readonly IFileStorage _storage;
    private readonly IBranchGuard _branchGuard;
    private readonly IBackupService _backup;

    public DeleteFileExecutor()
        : this(ImanFileStorageFactory.CreateStorage(), new BranchGuard(), new BackupService())
    {
    }

    internal DeleteFileExecutor(IFileStorage storage, IBranchGuard branchGuard, IBackupService backup)
    {
        _storage = storage;
        _branchGuard = branchGuard;
        _backup = backup;
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.DeleteFile;

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var delete = (DeleteFileCommand)command;

        var sandbox = new PathSandbox(context.WorkspaceRoot);
        var resolve = sandbox.Resolve(delete.Path);
        if (resolve.IsFailure)
            return CommandExecutionResult.Fail(command, resolve.Error.Code, resolve.Error.Message);

        var guard = await _branchGuard.EnsureModifiableAsync(context.WorkspaceRoot, context.Options.Git);
        if (guard.IsFailure)
            return CommandExecutionResult.Fail(command, guard.Error.Code, guard.Error.Message);

        if (!_storage.FileExists(resolve.Data))
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.FileNotFound,
                $"File not found: {delete.Path}");
        }

        if (context.DryRun)
            return NewFileExecutor.DryRunResult(command);

        // Deletion is confirmed in Ask AND AutoSafe; only Auto skips it. A backup is always made.
        if (context.Options.Mode != ApprovalMode.Auto)
        {
            context.Ui.ShowDiff(new FileChange(delete.Path, FileChangeKind.Delete, null, null));
            var confirmed = await context.Ui.ConfirmAsync(new ConfirmRequest(
                "Delete file",
                $"The model wants to delete \"{delete.Path}\". A backup is stored in .agent/backups."));
            if (!confirmed)
            {
                return CommandExecutionResult.Fail(command, ErrorCodes.CommandDeclined,
                    "The user declined this deletion.");
            }
        }

        var backup = await _backup.BackupAsync(context.WorkspaceRoot, resolve.Data);
        if (backup.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, backup.Error.Message);

        var remove = await _storage.DeleteAsync(resolve.Data);
        if (remove.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, remove.Error.Message);

        return CommandExecutionResult.Ok(command);
    }
}
