using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Abstractions.Models.Chat;

/// <summary>
/// Everything the chat provider needs to open (or resume) one chat session.
/// </summary>
public sealed record ChatSessionOptions
{
    /// <summary>
    /// Browser automation settings (CDP port, toggles, selectors file, timeouts, rate limits).
    /// </summary>
    public BrowserOptions Browser { get; init; } = new();

    /// <summary>
    /// Absolute path of the workspace root (for the selectors file, attachments, debug artifacts).
    /// </summary>
    public string WorkspaceRoot { get; init; } = string.Empty;

    /// <summary>
    /// URL of an existing chat to resume; null opens a new chat.
    /// </summary>
    public string? ResumeChatUrl { get; init; }

    /// <summary>
    /// Optional per-phase timing recorder (the --timing table). Null when timing is off.
    /// </summary>
    public ITimingRecorder? Timings { get; init; }

    /// <summary>
    /// Optional sink for session-level events (e.g. send attempts with their observed
    /// signals) — the agent loop wires this into the turn log. Null when no log is wanted.
    /// </summary>
    public Func<string, object, Task>? LogEvent { get; init; }
}
