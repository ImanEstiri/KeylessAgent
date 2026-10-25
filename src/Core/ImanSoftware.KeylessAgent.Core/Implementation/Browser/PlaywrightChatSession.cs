using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
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
    public async Task<Outcome> SendMessageAsync(string message, IReadOnlyList<SnapshotAttachment> attachments, CancellationToken cancellationToken = default)
    {
        try
        {
            await _rateLimiter.WaitAsync(cancellationToken);

            // --- Step 1: fingerprint the current newest assistant message (pre-send baseline) ---
            _preSendStamp = _page.GetNewestAssistantStamp();
            var itemCountBefore = _page.GetMessageItemCount();

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
            _page.InsertText(text);
            await Task.Delay(_random.Next(
                _options.Browser.MinDelayMs,
                Math.Max(_options.Browser.MinDelayMs + 1, _options.Browser.MaxDelayMs)), cancellationToken);

            if (!_page.TryClickSend())
                _page.PressEnter();

            // --- Step 4: confirm the send actually happened ---
            if (!await ConfirmSendAsync(itemCountBefore, cancellationToken))
            {
                // One retry: click, then Enter.
                _log.LogWarning("Send not confirmed within {Seconds}s; retrying once (click + Enter).", _options.Browser.SendConfirmSeconds);
                if (_page.TryClickSend())
                    _page.PressEnter();

                if (!await ConfirmSendAsync(itemCountBefore, cancellationToken))
                {
                    _page.SaveDebugScreenshot("send-not-confirmed");
                    return Outcome.Failure(new OutcomeError(
                        $"The message was not sent within {_options.Browser.SendConfirmSeconds + _options.Browser.SendConfirmSeconds}s (no new message item, and no generating indicator).",
                        ErrorCodes.SendNotConfirmed, OutcomeErrorType.Failure));
                }
            }

            return Outcome.Successful();
        }
        catch (Exception ex)
        {
            _page.SaveDebugScreenshot("send-failed");
            return Outcome.Failure(new OutcomeError(ex));
        }
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

            var stillGenerating = _page.IsStopIndicatorVisible();
            var stamp = _page.GetNewestAssistantStamp();
            var isNewMessage = stamp is not null && stamp.IsDifferentFrom(_preSendStamp);
            var reply = isNewMessage && _page.GetNewestAssistantHtml() is { } html
                ? _replyExtractor.ExtractFinalAnswer(html)
                : null;

            if (!stillGenerating &&
                isNewMessage &&
                !string.IsNullOrWhiteSpace(reply) &&
                reply == stableReply)
            {
                // This accepted message becomes the baseline for the next turn.
                _preSendStamp = stamp;
                return Outcome.Successful(reply);
            }

            // Only the NEW message's text may accumulate stability; the old reply never counts.
            stableReply = isNewMessage && !string.IsNullOrWhiteSpace(reply) ? reply : null;

            await Task.Delay(_pollDelayMs, cancellationToken);
        }

        _page.SaveDebugScreenshot("reply-timeout");
        return Outcome.Failure<string>(new OutcomeError(
            $"No complete new reply within {_options.Browser.ReplyTimeoutSeconds}s.",
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

            if (_page.GetMessageItemCount() > itemCountBefore)
                return true;

            if (_page.IsInputCleared() && _page.IsStopIndicatorVisible())
                return true;

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

        if (!_page.TryUploadFiles(created))
            return Outcome.Successful(AttachmentDelivery.FellBackToPaste);

        // The send click must wait until the upload landed: chips visible and send enabled.
        var deadline = DateTime.UtcNow.AddSeconds(_options.Browser.UploadWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_page.AreAttachmentChipsVisible() && _page.IsSendEnabled())
                return Outcome.Successful(AttachmentDelivery.Uploaded);

            await Task.Delay(Math.Min(_pollDelayMs, 250), cancellationToken);
        }

        _log.LogWarning("Upload did not settle within {Seconds}s; falling back to pasting.", _options.Browser.UploadWaitSeconds);
        return Outcome.Successful(AttachmentDelivery.FellBackToPaste);
    }

    private enum AttachmentDelivery
    {
        NothingToAttach,
        Uploaded,
        FellBackToPaste,
    }
}
