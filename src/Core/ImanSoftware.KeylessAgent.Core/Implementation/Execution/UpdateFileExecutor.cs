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
/// Replaces the whole content of an existing file (with backup and BOM preservation).
/// </summary>
internal sealed class UpdateFileExecutor : ICommandExecutor
{
    private readonly IFileStorage _storage;
    private readonly IBranchGuard _branchGuard;
    private readonly IBackupService _backup;

    public UpdateFileExecutor()
        : this(ImanFileStorageFactory.CreateStorage(), new BranchGuard(), new BackupService())
    {
    }

    internal UpdateFileExecutor(IFileStorage storage, IBranchGuard branchGuard, IBackupService backup)
    {
        _storage = storage;
        _branchGuard = branchGuard;
        _backup = backup;
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.UpdateFile;

    private static FileChange NormalizedChange(string path, FileChangeKind kind, string? oldContent, string newContent)
        => new(path, kind, oldContent is null ? null : LineEndings.NormalizeToLf(oldContent), LineEndings.NormalizeToLf(newContent));

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var update = (UpdateFileCommand)command;

        var sandbox = new PathSandbox(context.WorkspaceRoot);
        var resolve = sandbox.Resolve(update.Path);
        if (resolve.IsFailure)
            return CommandExecutionResult.Fail(command, resolve.Error.Code, resolve.Error.Message);

        var guard = await _branchGuard.EnsureModifiableAsync(context.WorkspaceRoot, context.Options.Git);
        if (guard.IsFailure)
            return CommandExecutionResult.Fail(command, guard.Error.Code, guard.Error.Message);

        if (!_storage.FileExists(resolve.Data))
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.FileNotFound,
                $"File not found: {update.Path}. Use newfile to create it.");
        }

        var oldContent = await _storage.ReadAllTextAsync(resolve.Data, context.CancellationToken);
        if (oldContent.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, oldContent.Error.Message);

        if (context.DryRun)
        {
            context.Ui.ShowDiff(NormalizedChange(update.Path, FileChangeKind.Update, oldContent.Data, update.Content));
            return NewFileExecutor.DryRunResult(command);
        }

        if (context.Options.Mode == ApprovalMode.Ask)
            context.Ui.ShowDiff(NormalizedChange(update.Path, FileChangeKind.Update, oldContent.Data, update.Content));

        var backup = await _backup.BackupAsync(context.WorkspaceRoot, resolve.Data);
        if (backup.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, backup.Error.Message);

        // The model's content is LF; the existing file keeps its own dominant ending.
        var ending = LineEndings.Detect(oldContent.Data);
        var write = await FileWriteHelpers.WritePreservingBomAsync(
            _storage, resolve.Data, LineEndings.Apply(LineEndings.NormalizeToLf(update.Content), ending));
        if (write.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, write.Error.Message);

        return CommandExecutionResult.Ok(command);
    }
}
