using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Prints the model's <c>say</c> message to the user through the UI contract.
/// </summary>
internal sealed class SayExecutor : ICommandExecutor
{
    /// <inheritdoc />
    public CommandKind Kind => CommandKind.Say;

    /// <inheritdoc />
    public Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context)
    {
        var say = (SayCommand)command;
        context.Ui.Say(say.Message);
        return Task.FromResult(CommandExecutionResult.Ok(command));
    }
}
