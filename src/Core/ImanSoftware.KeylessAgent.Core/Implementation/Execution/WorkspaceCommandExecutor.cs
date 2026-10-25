using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Dispatches a parsed command to its per-command executor. Unknown kinds become error results
/// instead of exceptions.
/// </summary>
internal sealed class WorkspaceCommandExecutor : IWorkspaceCommandExecutor
{
    private readonly Dictionary<CommandKind, ICommandExecutor> _executors;

    public WorkspaceCommandExecutor()
        : this(
            new SayExecutor(),
            new NewFileExecutor(),
            new UpdateFileExecutor(),
            new EditFileExecutor(),
            new DeleteFileExecutor(),
            new ReadFileExecutor(),
            new RunCommandExecutor(),
            new AskExecutor(),
            new CommitExecutor(),
            new DoneExecutor())
    {
    }

    internal WorkspaceCommandExecutor(params ICommandExecutor[] executors)
    {
        _executors = executors.ToDictionary(e => e.Kind);
    }

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        if (!_executors.TryGetValue(command.Kind, out var executor))
        {
            return CommandExecutionResult.Fail(command,
                ErrorCodes.Internal, $"No executor is registered for {command.Kind}.");
        }

        try
        {
            return await executor.ExecuteAsync(command, context);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CommandExecutionResult.Fail(command,
                ErrorCodes.Internal, $"Unexpected failure: {ex.Message}");
        }
    }
}
