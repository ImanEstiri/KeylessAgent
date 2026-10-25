using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;

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
}
