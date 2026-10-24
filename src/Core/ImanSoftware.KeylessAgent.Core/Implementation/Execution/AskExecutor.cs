using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Asks the user the model's question and returns the answer as the command result.
/// </summary>
internal sealed class AskExecutor : ICommandExecutor
{
    /// <inheritdoc />
    public CommandKind Kind => CommandKind.Ask;

    /// <inheritdoc />
    public async Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var ask = (AskCommand)command;
        var answer = await context.Ui.AskAsync(ask.Question);
        return new CommandExecutionResult
        {
            Number = command.Number,
            Kind = command.Kind,
            Target = command.Target,
            Status = CommandStatus.Ok,
            Output = $"User answered: {answer}",
        };
    }
}
