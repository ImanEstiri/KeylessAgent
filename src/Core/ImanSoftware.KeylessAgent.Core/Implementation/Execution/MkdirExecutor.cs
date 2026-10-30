using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Creates a folder (and missing parents). Sandboxed like the other file commands; never
/// confirmed (idempotent, harmless); journaled so keyless undo removes it while empty.
/// </summary>
internal sealed class MkdirExecutor : ICommandExecutor
{
    private readonly IFileStorage _storage;
    private readonly IBranchGuard _branchGuard;
    private readonly BackupService _backup;

    public MkdirExecutor()
        : this(ImanFileStorageFactory.CreateStorage(), new BranchGuard())
    {
    }

    internal MkdirExecutor(IFileStorage storage, IBranchGuard branchGuard, BackupService? backup = null)
    {
        _storage = storage;
        _branchGuard = branchGuard;
        _backup = backup ?? new BackupService();
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.Mkdir;

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var mkdir = (MkdirCommand)command;

        var sandbox = new PathSandbox(context.WorkspaceRoot);
        var resolve = sandbox.Resolve(mkdir.Path);
        if (resolve.IsFailure)
            return CommandExecutionResult.Fail(command, resolve.Error.Code, resolve.Error.Message);

        // NO-GIT MODE and DRY RUN: no branch protection (no git, or nothing is mutated).
        if (!context.NoGitMode && !context.DryRun)
        {
            var guard = await _branchGuard.EnsureModifiableAsync(context.WorkspaceRoot, context.Options.Git);
            if (guard.IsFailure)
                return CommandExecutionResult.Fail(command, guard.Error.Code, guard.Error.Message);
        }

        // Conflict ONLY when a FILE with that name exists; an existing folder is fine
        // (CreateDirectory is idempotent).
        if (_storage.FileExists(resolve.Data))
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.FileAlreadyExists,
                $"A file already exists at: {mkdir.Path}. Refusing to shadow it with a folder.");
        }

        if (context.DryRun)
            return NewFileExecutor.DryRunResult(command);

        var directory = await _storage.CreateDirectoryAsync(resolve.Data, context.CancellationToken);
        if (directory.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, directory.Error.Message);

        // Best-effort journal: keyless undo removes the created folder while it is empty.
        await _backup.RecordCreateDirectoryAsync(context.WorkspaceRoot, resolve.Data);

        return new CommandExecutionResult
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = $"Created folder {mkdir.Path}.",
        };
    }
}
