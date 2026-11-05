namespace ImanSoftware.KeylessAgent.Abstractions.Models.Agent;

/// <summary>
/// The persisted agent session state in <c>.agent/session.json</c>, powering <c>keyless resume</c>.
/// </summary>
public sealed record AgentSession
{
    /// <summary>
    /// The chat URL the session runs in. ONLY a conversation URL
    /// (https://chat.deepseek.com/a/chat/s/&lt;id&gt;) is ever persisted here — the bare base
    /// URL is stored as EMPTY instead (the base is not a conversation and must never be
    /// resumed or shared between workspaces).
    /// </summary>
    public string ChatUrl { get; init; } = string.Empty;

    /// <summary>
    /// UTC timestamp of the last time ChatUrl was confirmed/updated from the live page
    /// (after the first send of a chat and re-checked at every turn end). Empty when the
    /// conversation URL has not been observed yet.
    /// </summary>
    public string ChatUrlUpdatedUtc { get; init; } = string.Empty;

    /// <summary>
    /// The agent branch created for this session.
    /// </summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>
    /// How many user requests this session has processed.
    /// </summary>
    public int Turn { get; init; }

    /// <summary>
    /// Whether the system prompt was already sent to this chat (first message only).
    /// </summary>
    public bool SystemPromptSent { get; init; }

    /// <summary>
    /// UTC timestamp of session creation.
    /// </summary>
    public string CreatedAtUtc { get; init; } = string.Empty;

    /// <summary>
    /// UTC timestamp of the last activity.
    /// </summary>
    public string LastActivityUtc { get; init; } = string.Empty;

    /// <summary>
    /// Workspace-relative paths the AGENT changed (file commands plus runcommand-detected
    /// changes). Drives the dirty-menu skip.
    /// </summary>
    public IReadOnlyList<string> AgentChangedPaths { get; init; } = [];

    /// <summary>
    /// Turns served by the current chat (for Chat.MaxTurnsPerChat).
    /// </summary>
    public int TurnsInChat { get; init; }
}
