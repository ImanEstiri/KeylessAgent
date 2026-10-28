namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// A fingerprint of the newest assistant message, captured before every send so the reply
/// wait can tell the new reply apart from the previous one. The list is virtualized, so the
/// visible-count alone is never enough: the message key is the primary identity, with a
/// hash of the final-answer text as fallback.
/// </summary>
/// <param name="Key">The <c>data-virtual-list-item-key</c> of the newest assistant message, when present.</param>
/// <param name="TextHash">A stable hash of the newest final-answer text (0 when there is none).</param>
/// <param name="VisibleCount">How many assistant messages are currently in the DOM.</param>
internal sealed record AssistantStamp(string? Key, int TextHash, int VisibleCount)
{
    /// <summary>
    /// Whether <paramref name="other"/> is a different, later message than this one.
    /// When both carry message keys, only keys are compared (identical reply text is valid
    /// only when the key changed); otherwise the text hash and visible count decide.
    /// </summary>
    public bool IsDifferentFrom(AssistantStamp? other)
    {
        if (other is null)
            return true;

        if (Key is not null && other.Key is not null)
            return !string.Equals(Key, other.Key, StringComparison.Ordinal);

        return TextHash != other.TextHash || VisibleCount != other.VisibleCount;
    }
}

/// <summary>
/// The page operations the chat session needs — the seam that makes the reply-waiting logic
/// unit-testable against a fake page. All operations are async and accept a CancellationToken
/// so Ctrl+C interrupts Playwright waits.
/// </summary>
internal interface IChatPage
{
    /// <summary>The current chat URL.</summary>
    string Url { get; }

    /// <summary>Fingerprint of the newest assistant message (key, text hash, visible count).</summary>
    Task<AssistantStamp?> GetNewestAssistantStampAsync(CancellationToken cancellationToken = default);

    /// <summary>The outer HTML of the newest assistant message, or null when there is none.</summary>
    Task<string?> GetNewestAssistantHtmlAsync(CancellationToken cancellationToken = default);

    /// <summary>How many message items (user + assistant) are currently visible in the list.</summary>
    Task<int> GetMessageItemCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the stop/generating indicator is currently visible.</summary>
    Task<bool> IsStopIndicatorVisibleAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the input box is empty (the text was consumed by the send).</summary>
    Task<bool> IsInputClearedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The send control state: true/false when a send button matched (enabled/disabled),
    /// null when no send-control selector matched at all (undetectable — callers must not block on it).
    /// </summary>
    Task<bool?> IsSendEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether uploaded-file chips are visible (the upload has landed in the UI).</summary>
    Task<bool> AreAttachmentChipsVisibleAsync(CancellationToken cancellationToken = default);

    /// <summary>The file names the hidden file input currently reports (the upload was registered).</summary>
    Task<IReadOnlyList<string>> GetInputFileNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether an element near the composer (a chip or its label) shows the given file name.</summary>
    Task<bool> IsFileNameVisibleAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>Inserts the whole text into the input at once — never per-character typing.</summary>
    Task InsertTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Clicks the send button; false when the button cannot be clicked.</summary>
    Task<bool> TryClickSendAsync(CancellationToken cancellationToken = default);

    /// <summary>Presses Enter in the input box.</summary>
    Task PressEnterAsync(CancellationToken cancellationToken = default);

    /// <summary>Uploads files through the chat's file input; false when the input cannot be used.</summary>
    Task<bool> TryUploadFilesAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default);

    /// <summary>Saves a debug screenshot to the given absolute path (best-effort; may be a no-op).</summary>
    Task SaveDebugScreenshotAsync(string fullPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// The rendered text of the newest assistant message's reasoning block
    /// (.ds-think-content), or null when there is none.
    /// </summary>
    Task<string?> GetNewestReasoningTextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Hovers the newest assistant message and waits for its message-level Copy control.
    /// False when the control does not appear in time (the bar may be hover-only).
    /// </summary>
    Task<bool> WaitForMessageCopyButtonAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the clipboard interception (wraps navigator.clipboard.writeText and the
    /// execCommand('copy') path) gated on an agent-only flag; the user's manual copying is
    /// never affected and the OS clipboard is never written by interception.
    /// </summary>
    Task PrepareReplyCaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the newest message's copied final answer: hover, click its Copy control,
    /// prefer the intercepted text; when nothing is intercepted within the timeout, grant
    /// clipboard-read/write, save the current OS clipboard text, read
    /// navigator.clipboard.readText() and ALWAYS restore the previous text. Null when both
    /// strategies fail.
    /// </summary>
    Task<string?> TryCaptureCopiedReplyAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// The aria-pressed state of the first visible toggle matching one of the selectors
    /// ("true"/"false"/other raw attribute text), or null when no selector matched at all.
    /// </summary>
    Task<string?> GetToggleStateAsync(string[] selectors, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clicks the first visible toggle matching one of the selectors; true when a click landed.
    /// </summary>
    Task<bool> ClickToggleAsync(string[] selectors, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a chat-length-limit notice is currently visible (the chat is exhausted and
    /// cannot take more messages).
    /// </summary>
    Task<bool> IsChatLimitNoticeVisibleAsync(CancellationToken cancellationToken = default);
}
