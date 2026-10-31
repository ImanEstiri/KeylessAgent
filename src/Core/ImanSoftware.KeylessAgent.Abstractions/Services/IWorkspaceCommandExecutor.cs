using ImanSoftware.KeylessAgent.Abstractions.Models.Execution;
using ImanSoftware.KeylessAgent.Abstractions.Models.Parser;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Executes one parsed AgentScript command on the workspace and returns its result.
/// Behind this interface sit per-command executors; each one owns its own safety checks
/// (branch guard, path sandbox, command policy, backups) and never throws for expected failures.
/// </summary>
public interface IWorkspaceCommandExecutor : IService
{
    /// <summary>
    /// Executes a single command.
    /// </summary>
    /// <param name="command">The parsed command to execute.</param>
    /// <param name="context">The current run context (workspace, options, UI, cancellation).</param>
    /// <returns>The command's execution result, including blocked/declined outcomes.</returns>
    Task<CommandExecutionResult> ExecuteAsync(AgentScriptCommand command, AgentExecutionContext context);
}
