using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Marks the end of the agent turn; the loop stops after it. The summary is the command output.
/// </summary>
internal sealed class DoneExecutor : ICommandExecutor
{
    /// <inheritdoc />
    public CommandKind Kind => CommandKind.Done;

    /// <inheritdoc />
    public Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var done = (DoneCommand)command;
        return Task.FromResult(new CommandExecutionResult
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = done.Summary,
        });
    }
}
