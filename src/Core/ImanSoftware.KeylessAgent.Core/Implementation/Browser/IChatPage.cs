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
}
