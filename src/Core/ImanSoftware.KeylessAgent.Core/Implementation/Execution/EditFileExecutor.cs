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
/// Replaces the single occurrence of FIND with REPLACE in an existing file
/// (with backup and BOM preservation). Fails unless FIND matches exactly once.
/// </summary>
internal sealed class EditFileExecutor : ICommandExecutor
{
    private readonly IFileStorage _storage;
    private readonly IBranchGuard _branchGuard;
    private readonly IBackupService _backup;

    public EditFileExecutor()
        : this(ImanFileStorageFactory.CreateStorage(), new BranchGuard(), new BackupService())
    {
    }

    internal EditFileExecutor(IFileStorage storage, IBranchGuard branchGuard, IBackupService backup)
    {
        _storage = storage;
        _branchGuard = branchGuard;
        _backup = backup;
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.EditFile;

    private static FileChange NormalizedChange(string path, FileChangeKind kind, string? oldContent, string newContent)
        => new(path, kind, oldContent is null ? null : LineEndings.NormalizeToLf(oldContent), LineEndings.NormalizeToLf(newContent));

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var edit = (EditFileCommand)command;

        var sandbox = new PathSandbox(context.WorkspaceRoot);
        var resolve = sandbox.Resolve(edit.Path);
        if (resolve.IsFailure)
            return CommandExecutionResult.Fail(command, resolve.Error.Code, resolve.Error.Message);

        var guard = await _branchGuard.EnsureModifiableAsync(context.WorkspaceRoot, context.Options.Git);
        if (guard.IsFailure)
            return CommandExecutionResult.Fail(command, guard.Error.Code, guard.Error.Message);

        if (!_storage.FileExists(resolve.Data))
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.FileNotFound,
                $"File not found: {edit.Path}");
        }

        var content = await _storage.ReadAllTextAsync(resolve.Data, context.CancellationToken);
        if (content.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, content.Error.Message);

        // Workspace files are usually CRLF, model text is LF: match and count on normalized text.
        var ending = LineEndings.Detect(content.Data);
        var normalized = LineEndings.NormalizeToLf(content.Data);
        var find = LineEndings.NormalizeToLf(edit.Find);

        var count = CountOccurrences(normalized, find);
        if (count != 1)
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.FindNotUnique,
                $"FIND text matched {count} times (must match exactly once) in {edit.Path}.");
        }

        var newContent = LineEndings.Apply(
            ReplaceOnce(normalized, find, LineEndings.NormalizeToLf(edit.Replace)),
            ending);

        if (context.DryRun)
        {
            context.Ui.ShowDiff(NormalizedChange(edit.Path, FileChangeKind.Edit, content.Data, newContent));
            return NewFileExecutor.DryRunResult(command);
        }

        if (context.Options.Mode == ApprovalMode.Ask)
            context.Ui.ShowDiff(NormalizedChange(edit.Path, FileChangeKind.Edit, content.Data, newContent));

        var backup = await _backup.BackupAsync(context.WorkspaceRoot, resolve.Data);
        if (backup.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, backup.Error.Message);

        var write = await FileWriteHelpers.WritePreservingBomAsync(_storage, resolve.Data, newContent);
        if (write.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, write.Error.Message);

        return CommandExecutionResult.Ok(command);
    }

    internal static int CountOccurrences(string text, string find)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(find, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += find.Length;
        }

        return count;
    }

    private static string ReplaceOnce(string text, string find, string replace)
    {
        var index = text.IndexOf(find, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replace, text.AsSpan(index + find.Length));
    }
}
