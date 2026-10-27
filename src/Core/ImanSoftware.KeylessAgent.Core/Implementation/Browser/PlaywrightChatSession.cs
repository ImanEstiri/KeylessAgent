using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Logging;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// One live chat: rate-limited sends with attachment upload (paste fallback) and reply waiting
/// that can never return the PREVIOUS reply:
/// before every send the newest assistant message is fingerprinted (message key, or a hash of
/// the final-answer text plus visible count); after sending, the send itself is confirmed
/// (a new message item appeared, or the input is empty and the stop indicator is on), and a
/// reply is accepted only when its fingerprint differs from the pre-send one, its final-answer
/// element exists and is stable for two reads one poll apart, and the stop indicator is gone.
/// </summary>
internal sealed class PlaywrightChatSession : IChatSession
{
    private readonly IChatPage _page;
    private readonly Func<ValueTask> _disposeAsync;
    private readonly ChatSessionOptions _options;
    private readonly IReplyExtractor _replyExtractor;
    private readonly ILoggerAdapter<PlaywrightChatSession> _log;
    private readonly RateLimiter _rateLimiter;
    private readonly IFileStorage _storage;
    private readonly Random _random = new();
    private readonly int _pollDelayMs;

    /// <summary>
    /// The fingerprint captured before the last send; the reply wait only accepts a newer message.
    /// </summary>
    private AssistantStamp? _preSendStamp;

    public PlaywrightChatSession(
        IChatPage page,
        Func<ValueTask> disposeAsync,
        ChatSessionOptions options,
        IReplyExtractor replyExtractor,
        RateLimiter rateLimiter,
        ILoggerAdapter<PlaywrightChatSession> log,
        IFileStorage storage,
        int pollDelayMs = 1_000)
    {
        _page = page;
        _disposeAsync = disposeAsync;
        _options = options;
        _replyExtractor = replyExtractor;
        _rateLimiter = rateLimiter;
        _log = log;
        _storage = storage;
        _pollDelayMs = pollDelayMs;
    }

    /// <inheritdoc />
    public string ChatUrl => _page.Url;

    /// <inheritdoc />
    public string LastReplyOrigin => LastReplyOriginValue switch
    {
        ReplyOrigin.Copy => "Copy",
        ReplyOrigin.DomAfterRejectedCopy => "Dom (copy rejected)",
        _ => "Dom",
    };

    private ReplyOrigin LastReplyOriginValue { get; set; } = ReplyOrigin.Dom;

    /// <inheritdoc />
    public async Task<Outcome> SendMessageAsync(string message, IReadOnlyList<SnapshotAttachment> attachments, CancellationToken cancellationToken = default)
    {
        try
        {
            await _rateLimiter.WaitAsync(cancellationToken);

            // --- Step 1: fingerprint the current newest assistant message (pre-send baseline) ---
            _preSendStamp = await _page.GetNewestAssistantStampAsync(cancellationToken);
            var itemCountBefore = await _page.GetMessageItemCountAsync(cancellationToken);

            // --- Step 2: deliver attachments (upload with wait, paste fallback) ---
            var text = message;
            var delivery = await DeliverAttachmentsAsync(attachments, cancellationToken);
            if (delivery.IsFailure)
                return delivery;
            if (delivery.Data == AttachmentDelivery.FellBackToPaste)
            {
                _log.LogWarning("Attachment upload unavailable; pasting the content into the message.");
                text = message + "\n\n" + string.Join("\n\n", attachments.Select(a =>
                    $"--- attached: {a.Name} ---\n{a.Content}"));
            }

            // --- Step 3: insert the whole text and send ---
            await _page.InsertTextAsync(text, cancellationToken);
            await Task.Delay(_random.Next(
                _options.Browser.MinDelayMs,
                Math.Max(_options.Browser.MinDelayMs + 1, _options.Browser.MaxDelayMs)), cancellationToken);

            // One send attempt = the send button when clickable, otherwise Enter — never both.
            if (!await _page.TryClickSendAsync(cancellationToken))
                await _page.PressEnterAsync(cancellationToken);

            // --- Step 4: confirm the send actually happened ---
            if (!await ConfirmSendAsync(itemCountBefore, cancellationToken))
            {
                // The list is virtualized: a constant item count proves nothing. An emptied input
                // means the text left the composer — the message was sent.
                if (await _page.IsInputClearedAsync(cancellationToken))
                {
                    _log.LogInformation("Send not confirmed by the message list, but the input was cleared; treating the message as sent.");
                    return Outcome.Successful();
                }

                // The text is still in the composer: exactly one retry, with ENTER. The button
                // click was already proven ineffective (or it would have been confirmed), and
                // re-clicking a mis-identified control (e.g. the paperclip) only opens dialogs.
                _log.LogWarning("Send not confirmed within {Seconds}s and the input still holds the text; retrying once with Enter.", _options.Browser.SendConfirmSeconds);
                await _page.PressEnterAsync(cancellationToken);

                if (!await ConfirmSendAsync(itemCountBefore, cancellationToken))
                {
                    if (await _page.IsInputClearedAsync(cancellationToken))
                    {
                        _log.LogInformation("Send confirmed after retry by the cleared input.");
                        return Outcome.Successful();
                    }

                    var screenshot = await CaptureDiagnosticsAsync("send-not-confirmed", cancellationToken);
                    return Outcome.Failure(new OutcomeError(
                        $"The message was not sent within {2 * _options.Browser.SendConfirmSeconds}s (input still holds the text, no generating indicator)."
                        + DiagnosticsHint(screenshot),
                        ErrorCodes.SendNotConfirmed, OutcomeErrorType.Failure));
                }
            }

            return Outcome.Successful();
        }
        catch (Exception ex)
        {
            await CaptureDiagnosticsAsync("send-failed", cancellationToken);
            return Outcome.Failure(new OutcomeError(ex));
        }
    }


    private bool _capturePrepared;

    /// <summary>The origin of the last accepted reply (for the turn log).</summary>
    /// <summary>
    /// Decides the reply source: Dom = rendered text; Copy = capture (hover + click newest
    /// message's Copy control, interception then OS clipboard with restore) validated against
    /// the DOM text; Auto = copy first, fall back to Dom on capture/validation failure.
    /// </summary>
    private async Task<Outcome<ReplyReadResult>> ReadReplyTextAsync(string domText, CancellationToken cancellationToken)
    {
        var mode = _options.Browser.ReplySource;
        if (mode == ReplySourceMode.Dom)
        {
            LastReplyOriginValue = ReplyOrigin.Dom;
            return Outcome.Successful(new ReplyReadResult(domText, ReplyOrigin.Dom));
        }

        // ExposeBinding must be installed exactly once, before the first capture.
        if (!_capturePrepared)
        {
            await _page.PrepareReplyCaptureAsync(cancellationToken);
            _capturePrepared = true;
        }

        // The message-level Copy control may appear on hover only; wait within the reply timeout.
        var buttonWait = await _page.WaitForMessageCopyButtonAsync(
            TimeSpan.FromSeconds(Math.Min(_options.Browser.ReplyTimeoutSeconds, 10)), cancellationToken);
        if (!buttonWait)
        {
            if (mode == ReplySourceMode.Copy)
            {
                return Outcome.Failure<ReplyReadResult>(new OutcomeError(
                    "The message Copy button did not appear within the reply timeout.",
                    ErrorCodes.ReplyTimeout, OutcomeErrorType.Failure));
            }

            LastReplyOriginValue = ReplyOrigin.Dom;
            return Outcome.Successful(new ReplyReadResult(domText, ReplyOrigin.Dom));
        }

        var reasoning = await _page.GetNewestReasoningTextAsync(cancellationToken);
        var read = await ReplyReader.ReadAsync(
            mode,
            new ReplyReadContext(domText, reasoning, ct => _page.TryCaptureCopiedReplyAsync(TimeSpan.FromSeconds(5), ct)),
            summary => _log.LogInformation("Reply copy validation: {Summary}", summary),
            cancellationToken);

        if (read.IsFailure)
            return read;

        LastReplyOriginValue = read.Data.Origin;
        return read;
    }

    /// <inheritdoc />
    public async Task<Outcome<string>> WaitForReplyAsync(CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(_options.Browser.ReplyTimeoutSeconds);
        var deadline = DateTime.UtcNow + timeout;
        var stableReply = (string?)null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stillGenerating = await _page.IsStopIndicatorVisibleAsync(cancellationToken);
            var stamp = await _page.GetNewestAssistantStampAsync(cancellationToken);
            var isNewMessage = stamp is not null && stamp.IsDifferentFrom(_preSendStamp);
            var reply = isNewMessage && await _page.GetNewestAssistantHtmlAsync(cancellationToken) is { } html
                ? _replyExtractor.ExtractFinalAnswer(html)
                : null;

            if (!stillGenerating &&
                isNewMessage &&
                !string.IsNullOrWhiteSpace(reply) &&
                reply == stableReply)
            {
                // Completion detected. The DOM text is the baseline; the reply SOURCE (raw
                // Markdown from the Copy button vs DOM extraction) is decided per config.
                var readOutcome = await ReadReplyTextAsync(reply, cancellationToken);
                if (readOutcome.IsFailure)
                    return Outcome.Failure<string>(readOutcome.Error);

                _preSendStamp = stamp;
                return Outcome.Successful(readOutcome.Data.Text);
            }

            // Only the NEW message's text may accumulate stability; the old reply never counts.
            stableReply = isNewMessage && !string.IsNullOrWhiteSpace(reply) ? reply : null;

            await Task.Delay(_pollDelayMs, cancellationToken);
        }

        var screenshot = await CaptureDiagnosticsAsync("reply-timeout", cancellationToken);
        return Outcome.Failure<string>(new OutcomeError(
            $"No complete new reply within {_options.Browser.ReplyTimeoutSeconds}s." + DiagnosticsHint(screenshot),
            ErrorCodes.ReplyTimeout, OutcomeErrorType.Failure));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _disposeAsync();

    /// <summary>
    /// A send is confirmed when a new message item appeared in the DOM, or the input was
    /// consumed and the stop/generating indicator is on.
    /// </summary>
    private async Task<bool> ConfirmSendAsync(int itemCountBefore, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_options.Browser.SendConfirmSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await _page.GetMessageItemCountAsync(cancellationToken) > itemCountBefore)
                return true;

            if (await _page.IsInputClearedAsync(cancellationToken) &&
                await _page.IsStopIndicatorVisibleAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(Math.Min(_pollDelayMs, 250), cancellationToken);
        }

        return false;
    }

    private async Task<Outcome<AttachmentDelivery>> DeliverAttachmentsAsync(IReadOnlyList<SnapshotAttachment> attachments, CancellationToken cancellationToken)
    {
        if (attachments.Count == 0)
            return Outcome.Successful(AttachmentDelivery.NothingToAttach);

        var directory = Path.Combine(_options.WorkspaceRoot, ".agent", "attachments");
        var created = new List<string>();
        foreach (var attachment in attachments)
        {
            var path = Path.Combine(directory, attachment.Name);
            var mkdir = await _storage.CreateDirectoryAsync(directory, cancellationToken);
            if (mkdir.IsFailure)
                return Outcome.Failure<AttachmentDelivery>(mkdir.Error);

            var write = await _storage.WriteAllTextAsync(path, attachment.Content, cancellationToken);
            if (write.IsFailure)
                return Outcome.Failure<AttachmentDelivery>(write.Error);

            created.Add(path);
        }

        if (!await _page.TryUploadFilesAsync(created, cancellationToken))
        {
            _log.LogWarning("SetInputFiles failed; pasting the attachment content instead.");
            return Outcome.Successful(AttachmentDelivery.FellBackToPaste);
        }

        // SetInputFiles succeeded: that proves the files were SELECTED, not that the upload
        // finished. Wait for both: a positive upload sign (files registered / chips visible)
        // AND an enabled send control, as long as the send control can be detected at all
        // (when none of its selectors match, do not block on it — Enter is the fallback).
        var uploadNames = attachments.Select(a => a.Name).ToList();
        var deadline = DateTime.UtcNow.AddSeconds(_options.Browser.UploadWaitSeconds);
        var uploadSign = false;
        var sendSeen = false;
        var sendEnabled = false;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!uploadSign && await HasUploadSignAsync(uploadNames, cancellationToken))
            {
                uploadSign = true;
                _log.LogInformation("Upload registered in the composer.");
            }

            var sendState = await _page.IsSendEnabledAsync(cancellationToken);
            if (sendState.HasValue)
            {
                sendSeen = true;
                sendEnabled = sendState.Value;
            }

            if (uploadSign && (!sendSeen || sendEnabled))
                break;

            await Task.Delay(Math.Min(_pollDelayMs, 250), cancellationToken);
        }

        // Final sweep after the loop: the last poll may have flipped both flags.
        if (!uploadSign)
            uploadSign = await HasUploadSignAsync(uploadNames, cancellationToken);
        if (!sendSeen)
        {
            var sendState = await _page.IsSendEnabledAsync(cancellationToken);
            if (sendState.HasValue)
            {
                sendSeen = true;
                sendEnabled = sendState.Value;
            }
        }

        if (uploadSign && (!sendSeen || sendEnabled))
        {
            if (!sendEnabled)
                _log.LogWarning("Upload registered but the send control is still disabled after {Seconds}s; sending anyway.", _options.Browser.UploadWaitSeconds);
            return Outcome.Successful(AttachmentDelivery.Uploaded);
        }

        if (uploadSign && !sendSeen)
            return Outcome.Successful(AttachmentDelivery.Uploaded);

        _log.LogWarning("No sign of the uploaded files within {Seconds}s; pasting the content instead.", _options.Browser.UploadWaitSeconds);
        return Outcome.Successful(AttachmentDelivery.FellBackToPaste);
    }

    /// <summary>
    /// Whether the page shows any sign that the upload landed: the hidden file input reports
    /// one of the files, a composer-near element shows a file name, or chips are visible.
    /// </summary>
    private async Task<bool> HasUploadSignAsync(List<string> uploadNames, CancellationToken cancellationToken)
    {
        var inputFiles = await _page.GetInputFileNamesAsync(cancellationToken);
        if (inputFiles.Any(file => uploadNames.Contains(file, StringComparer.OrdinalIgnoreCase)))
            return true;

        foreach (var name in uploadNames)
        {
            if (await _page.IsFileNameVisibleAsync(name, cancellationToken))
                return true;
        }

        return await _page.AreAttachmentChipsVisibleAsync(cancellationToken);
    }

    /// <summary>
    /// Debug mode captures a page screenshot (via the debug-capable page implementations) and
    /// the error message points at the file; otherwise the message points at keyless doctor.
    /// </summary>
    private async Task<string?> CaptureDiagnosticsAsync(string name, CancellationToken cancellationToken)
    {
        if (!_options.Browser.Debug)
            return null;

        try
        {
            var directory = Path.Combine(_options.WorkspaceRoot, ".agent", "logs");
            var mkdir = await _storage.CreateDirectoryAsync(directory, cancellationToken);
            if (mkdir.IsFailure)
                return null;

            var path = Path.Combine(directory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await _page.SaveDebugScreenshotAsync(path, cancellationToken);
            _log.LogInformation("Debug screenshot saved: {Path}", path);
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Screenshots are best-effort.
            return null;
        }
    }

    private static string DiagnosticsHint(string? screenshotPath)
        => screenshotPath is not null
            ? $" Debug screenshot: {screenshotPath}"
            : " Run 'keyless doctor' to inspect the live selectors.";

    private enum AttachmentDelivery
    {
        NothingToAttach,
        Uploaded,
        FellBackToPaste,
    }
}
