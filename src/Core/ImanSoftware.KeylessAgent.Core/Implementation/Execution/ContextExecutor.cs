using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Control directive: selects the workspace-context mode of a FUTURE message. No filesystem
/// effect and never confirmed — the AgentLoop records the selection and the next outgoing
/// message that carries context uses it.
/// </summary>
internal sealed class ContextExecutor : ICommandExecutor
{
    /// <inheritdoc />
    public CommandKind Kind => CommandKind.Context;

    /// <inheritdoc />
    public Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var selection = (ContextCommand)command;
        return Task.FromResult(new CommandExecutionResult
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = "next context: " + selection.Mode,
        });
    }
}
