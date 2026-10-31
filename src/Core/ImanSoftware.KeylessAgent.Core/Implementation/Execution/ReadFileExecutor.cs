using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Reads a workspace file and returns its content to the model as the command result.
/// </summary>
internal sealed class ReadFileExecutor : ICommandExecutor
{
    private readonly IFileStorage _storage;

    public ReadFileExecutor()
        : this(ImanFileStorageFactory.CreateStorage())
    {
    }

    internal ReadFileExecutor(IFileStorage storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public CommandKind Kind => CommandKind.ReadFile;

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var read = (ReadFileCommand)command;

        var sandbox = new PathSandbox(context.WorkspaceRoot);
        var resolve = sandbox.Resolve(read.Path);
        if (resolve.IsFailure)
            return CommandExecutionResult.Fail(command, resolve.Error.Code, resolve.Error.Message);

        if (!_storage.FileExists(resolve.Data))
        {
            return CommandExecutionResult.Fail(command, ErrorCodes.FileNotFound,
                $"File not found: {read.Path}");
        }

        var content = await _storage.ReadAllTextAsync(resolve.Data, context.CancellationToken);
        if (content.IsFailure)
            return CommandExecutionResult.Fail(command, ErrorCodes.FileIoFailure, content.Error.Message);

        return new CommandExecutionResult
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = content.Data,
        };
    }
}
