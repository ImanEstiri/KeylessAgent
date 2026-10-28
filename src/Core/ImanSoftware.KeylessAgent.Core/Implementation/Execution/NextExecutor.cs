using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Control directive: run this batch, send results, keep waiting. No work of its own — the
/// loop-level semantics (send results, stay open) are handled by the AgentLoop.
/// </summary>
internal sealed class NextExecutor : ICommandExecutor
{
    /// <inheritdoc />
    public CommandKind Kind => CommandKind.Next;

    /// <inheritdoc />
    public Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var next = (NextCommand)command;
        return Task.FromResult(new CommandExecutionResult
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = string.IsNullOrEmpty(next.Message) ? "waiting for the next reply" : next.Message,
        });
    }
}
