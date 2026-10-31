using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// One AgentScript command kind and how to execute it. Implemented by small, focused executors;
/// <see cref="WorkspaceCommandExecutor"/> dispatches to them.
/// </summary>
internal interface ICommandExecutor
{
    /// <summary>
    /// Which command this executor handles.
    /// </summary>
    CommandKind Kind { get; }

    /// <summary>
    /// Executes the command. Never throws for expected failures — every failure is a result.
    /// </summary>
    /// <param name="command">The parsed command (already validated).</param>
    /// <param name="context">The current run context.</param>
    Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context);
}
