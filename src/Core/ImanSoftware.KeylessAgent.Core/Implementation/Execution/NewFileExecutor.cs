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
/// Creates a new file. Fails when the file already exists; creates missing parent folders first.
/// </summary>
internal sealed class NewFileExecutor : ICommandExecutor
{
    private readonly IFileStorage _storage;
    private readonly IBranchGuard _branchGuard;

    public NewFileExecutor()
        : this(ImanFileStorageFactory.CreateStorage(), new BranchGuard())
    {
    }

    internal NewFileExecutor(IFileStorage storage, IBranchGuard branchGuard)
    {
        _storage = storage;
        _branchGuard = branchGuard;
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.NewFile;

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var newFile = (NewFileCommand)command;

        var sandbox = new PathSandbox(context.WorkspaceRoot);
        var resolve = sandbox.Resolve(newFile.Path);
        if (resolve.IsFailure)
            return CommandExecutionResult.Fail(command, resolve.Error.Code, resolve.Error.Message);

        // NO-GIT MODE and DRY RUN: no branch protection (no git, or nothing is mutated).
        if (!context.NoGitMode && !context.DryRun)
        {
            var guard = await _branchGuard.EnsureModifiableAsync(context.WorkspaceRoot, context.Options.Git);
            if (guard.IsFailure)
                return CommandExecutionResult.Fail(command, guard.Error.Code, guard.Error.Message);
        }

        if (_storage.FileExists(resolve.Data))
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.FileAlreadyExists,
                $"File already exists: {newFile.Path}. Use updatefile or editfile to change it.");
        }

        if (context.DryRun)
        {
            context.Ui.ShowDiff(NormalizedChange(newFile.Path, FileChangeKind.Create, null, newFile.Content));
            return DryRunResult(command);
        }

        if (context.Options.Mode == ApprovalMode.Ask)
            context.Ui.ShowDiff(NormalizedChange(newFile.Path, FileChangeKind.Create, null, newFile.Content));

        // The storage does not create parent directories implicitly.
        var directory = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(resolve.Data)!, context.CancellationToken);
        if (directory.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, directory.Error.Message);

        var ending = LineEndings.ResolveNewFileEnding(context.WorkspaceRoot, resolve.Data);
        var write = await _storage.WriteAllTextAsync(
            resolve.Data, LineEndings.Apply(LineEndings.NormalizeToLf(newFile.Content), ending), context.CancellationToken);
        if (write.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, write.Error.Message);

        return CommandExecutionResult.Ok(command);
    }

    private static FileChange NormalizedChange(string path, FileChangeKind kind, string? oldContent, string newContent)
        => new(path, kind, oldContent is null ? null : LineEndings.NormalizeToLf(oldContent), LineEndings.NormalizeToLf(newContent));

    internal static CommandExecutionResult DryRunResult(AgentScriptCommand command)
        => new()
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = "Dry run: no changes were made.",
        };
}
