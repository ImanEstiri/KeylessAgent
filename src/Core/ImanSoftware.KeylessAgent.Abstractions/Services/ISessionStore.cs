using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Persists the agent session state in <c>.agent/session.json</c> (chat URL, branch, turn counter),
/// so a later <c>keyless resume</c> reuses the same chat.
/// </summary>
public interface ISessionStore : IService
{
    /// <summary>
    /// Reads the saved session, if any.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>The session, or a <c>KA_SESSION_NOT_FOUND</c> failure when none exists yet.</returns>
    Task<Outcome<AgentSession>> LoadAsync(string workspaceRoot);

    /// <summary>
    /// Saves (or updates) the session.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="session">The session state to persist.</param>
    Task<Outcome> SaveAsync(string workspaceRoot, AgentSession session);

    /// <summary>
    /// Removes the saved session (after the chat is exhausted or the user starts over).
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    Task<Outcome> ClearAsync(string workspaceRoot);
}
