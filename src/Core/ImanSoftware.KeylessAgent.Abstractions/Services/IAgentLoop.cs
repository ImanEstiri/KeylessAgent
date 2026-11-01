using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// The public entry point every UI uses to run the agent: one call = one user request (one turn).
/// Everything the user sees during the turn flows back through <see cref="IAgentUserInterface"/>.
/// </summary>
public interface IAgentLoop : IService
{
    /// <summary>
    /// Runs one full agent turn for <paramref name="request"/>:
    /// git setup, snapshot, chat message + attachments, reply validation (with automatic re-ask),
    /// command execution with per-command results, and a snapshot refresh at the end — always.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory (a git repository).</param>
    /// <param name="request">The user's request text.</param>
    /// <param name="ui">The UI contract that shows progress, says, questions and diffs.</param>
    /// <param name="dryRun">Print what would happen; execute nothing.</param>
    /// <param name="cancellationToken">Cancels the turn (the snapshot still refreshes).</param>
    /// <param name="refreshEnv">Ignore the cached environment (<c>.agent/env.json</c>) and re-probe.</param>
    /// <param name="paceOverride">Overrides <c>Agent.Pace</c> from keyless.json for this turn (CLI/WPF toggle).</param>
    /// <returns>The turn result (branch, commits, summary, stop reason).</returns>
    Task<AgentTurnResult> RunAsync(
        string workspaceRoot,
        string request,
        IAgentUserInterface ui,
        bool dryRun = false,
        CancellationToken cancellationToken = default,
        bool refreshEnv = false,
        AgentPace? paceOverride = null);
}
