using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Opens chat sessions in the user's logged-in DeepSeek web chat.
/// The browser lives behind this interface so a future official-API provider can be added
/// without touching the agent loop.
/// </summary>
public interface IChatProvider : IService
{
    /// <summary>
    /// Connects to the user's Chrome and opens (or resumes) a chat.
    /// </summary>
    /// <param name="options">Session options (browser settings, workspace root, resume URL).</param>
    /// <param name="cancellationToken">Cancellation for the whole session.</param>
    /// <returns>A live chat session, or a <c>KA_BROWSER_*</c> failure.</returns>
    Task<Outcome<IChatSession>> StartSessionAsync(ChatSessionOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// One live chat in the user's browser. Messages are rate-limited and replies are read
/// from the final-answer element only — never from the reasoning block.
/// </summary>
public interface IChatSession : IAsyncDisposable
{
    /// <summary>
    /// The chat URL (persisted in <c>.agent/session.json</c> for <c>keyless resume</c>).
    /// </summary>
    string ChatUrl { get; }

    /// <summary>
    /// Where the last accepted reply was read from: "Copy", "Dom" or "Dom (copy rejected)".
    /// </summary>
    string LastReplyOrigin { get; }

    /// <summary>
    /// Sends one message. Attachments are uploaded through the chat's file input when possible
    /// and pasted into the message otherwise.
    /// </summary>
    /// <param name="message">The message text.</param>
    /// <param name="attachments">Snapshot attachments to send along (may be empty).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success or a failure with a <c>KA_</c> code.</returns>
    Task<Outcome> SendMessageAsync(string message, IReadOnlyList<SnapshotAttachment> attachments, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until the model finished its reply and returns the final-answer text
    /// (AgentScript), never the reasoning.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reply text, or <c>KA_REPLY_TIMEOUT</c>/<c>KA_SELECTOR_NOT_FOUND</c> failures.</returns>
    Task<Outcome<string>> WaitForReplyAsync(CancellationToken cancellationToken = default);
}
