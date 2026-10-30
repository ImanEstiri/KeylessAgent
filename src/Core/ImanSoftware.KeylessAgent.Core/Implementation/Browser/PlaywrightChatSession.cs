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

    /// <summary>Whether the DeepThink/Search toggles were already handled for this chat.</summary>
    private bool _togglesApplied;

    public PlaywrightChatSession(
        IChatPage page,
        Func<ValueTask> disposeAsync,
        ChatSessionOptions options,
        IReplyExtractor replyExtractor,
        RateLimiter rateLimiter,
        ILoggerAdapter<PlaywrightChatSession> log,
        IFileStorage storage,
        int pollDelayMs = 100)
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
        ReplyOrigin.DomAfterRejectedCopy => "Dom: copy rejected by validation",
        _ => LastReplyReason is null ? "Dom" : $"Dom: {LastReplyReason}",
    };

    private ReplyOrigin LastReplyOriginValue { get; set; } = ReplyOrigin.Dom;

    /// <summary>Why the reply came from the DOM instead of the Copy button, when it did.</summary>
    private string? LastReplyReason { get; set; }

    /// <inheritdoc />
    public async Task<Outcome> SendMessageAsync(string message, IReadOnlyList<SnapshotAttachment> attachments, CancellationToken cancellationToken = default)
    {
        try
        {
            await _rateLimiter.WaitAsync(cancellationToken);

            // Feature toggles are per-chat switches: set them once on the first message of a
            // NEW chat. A new chat has no resume URL (the loop leaves it empty when the saved
            // session was cleared); a resumed chat keeps whatever it was left with.
            if (!_togglesApplied)
            {
                _togglesApplied = true;
                if (string.IsNullOrEmpty(_options.ResumeChatUrl))
                    await ApplyChatTogglesAsync(cancellationToken);
            }

            // --- Step 1: fingerprint the current newest assistant message (pre-send baseline) ---
            _preSendStamp = await _page.GetNewestAssistantStampAsync(cancellationToken);
            var itemCountBefore = await _page.GetMessageItemCountAsync(cancellationToken);

            // --- Step 2: deliver attachments (upload with wait, paste fallback) ---
            var text = message;
            _options.Timings?.Start("upload");
            Outcome<AttachmentDelivery> delivery;
            try
            {
                delivery = await DeliverAttachmentsAsync(attachments, cancellationToken);
            }
            finally
            {
                _options.Timings?.Stop("upload");
            }

            if (delivery.IsFailure)
                return delivery;
            if (delivery.Data == AttachmentDelivery.FellBackToPaste)
            {
                _options.Timings?.Start("paste");
                _log.LogWarning("Attachment upload unavailable; pasting the content into the message.");
                text = message + "\n\n" + string.Join("\n\n", attachments.Select(a =>
                    $"--- attached: {a.Name} ---\n{a.Content}"));
                _options.Timings?.Stop("paste");
            }

            // --- Step 3: insert the whole text and send ---
            await _page.InsertTextAsync(text, cancellationToken);
            if (_options.Browser.HumanDelay)
            {
                await Task.Delay(_random.Next(
                    _options.Browser.MinDelayMs,
                    Math.Max(_options.Browser.MinDelayMs + 1, _options.Browser.MaxDelayMs)), cancellationToken);
            }

            var itemStampBefore = await _page.GetNewestItemStampAsync(cancellationToken);
            var sentPrefix = SentPrefixOf(text);

            // Two attempts max: attempt 1 clicks the send control (Enter when the click is
            // impossible), attempt 2 is the single Enter retry. Each attempt gets its own
            // confirm window; between them the signals are re-checked so a slow message is
            // never followed by a duplicate.
            for (var attempt = 1; ; attempt++)
            {
                _options.Timings?.Start("send");
                try
                {
                    if (attempt == 1)
                    {
                        // Require an enabled send control when it can be detected (as for
                        // uploads): clicking a detectably-disabled control is a wasted attempt.
                        var enableDeadline = DateTime.UtcNow.AddSeconds(2);
                        while (DateTime.UtcNow < enableDeadline)
                        {
                            var state = await _page.IsSendEnabledAsync(cancellationToken);
                            if (state is null or true)
                                break;
                            await Task.Delay(100, cancellationToken);
                        }
                    }

                    if (attempt == 1)
                    {
                        if (!await _page.TryClickSendAsync(cancellationToken))
                            await _page.PressEnterAsync(cancellationToken);
                    }
                    else
                    {
                        await _page.PressEnterAsync(cancellationToken);
                    }
                }
                finally
                {
                    _options.Timings?.Stop("send");
                }

                // --- Confirm the send actually happened ---
                var attemptName = $"send-confirm#{attempt}";
                _options.Timings?.Start(attemptName);
                bool confirmed;
                SendSignals signals;
                var confirmWatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    (confirmed, signals) = await ConfirmSendAsync(itemStampBefore, sentPrefix, cancellationToken);
                }
                finally
                {
                    _options.Timings?.Stop(attemptName);
                }

                var itemCountAfter = await _page.GetMessageItemCountAsync(cancellationToken);
                _log.LogWarning(
                    "Send attempt #{Attempt} {Outcome}: items {Before}->{After}, fingerprintChanged {Fingerprint}, userTextMatched {UserText}, inputCleared {Cleared}, chips {Chips}, stopVisible {Stop}, elapsed {Elapsed:N1}s.",
                    attempt, confirmed ? "confirmed" : "unconfirmed", itemCountBefore, itemCountAfter,
                    signals.FingerprintChanged, signals.UserTextMatched, signals.InputCleared,
                    signals.Chips, signals.StopVisible, confirmWatch.Elapsed.TotalSeconds);
                if (_options.LogEvent is not null)
                    await _options.LogEvent("send-attempt", new
                    {
                        Attempt = attempt,
                        Confirmed = confirmed,
                        ItemCountBefore = itemCountBefore,
                        ItemCountAfter = itemCountAfter,
                        signals.FingerprintChanged,
                        signals.UserTextMatched,
                        signals.InputCleared,
                        signals.Chips,
                        signals.StopVisible,
                    });

                if (confirmed)
                    return Outcome.Successful();

                // Re-check BEFORE any retry: a slow message may have landed after the window
                // closed — resending it would create a duplicate.
                var late = await ObserveSendSignalsAsync(itemStampBefore, sentPrefix, cancellationToken);
                if (Confirmed(late))
                {
                    _log.LogInformation("Send confirmed after the confirm window closed (signals arrived late); no retry.");
                    return Outcome.Successful();
                }

                // The text left the composer: the message was sent even though the confirm
                // signals stayed silent (same rule as the confirm window, applied late).
                if (await _page.IsInputClearedAsync(cancellationToken))
                {
                    _log.LogInformation("Send treated as delivered: the input no longer holds the text (attachment still attached if any).");
                    return Outcome.Successful();
                }

                if (attempt == 2)
                {
                    var screenshot = await CaptureDiagnosticsAsync("send-not-confirmed", cancellationToken);
                    return Outcome.Failure(new OutcomeError(
                        $"The message was not sent within {2 * _options.Browser.SendConfirmSeconds}s (input still holds the text, no generating indicator)."
                        + DiagnosticsHint(screenshot),
                        ErrorCodes.SendNotConfirmed, OutcomeErrorType.Failure));
                }

                // The text is still in the composer: exactly one retry, with ENTER. The button
                // click was already proven ineffective (or it would have been confirmed), and
                // re-clicking a mis-identified control (e.g. the paperclip) only opens dialogs.
                _log.LogWarning("Send not confirmed within {Seconds}s and the input still holds the text; retrying once with Enter.", _options.Browser.SendConfirmSeconds);
            }
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
            LastReplyReason = "Browser.ReplySource=Dom";
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
            LastReplyReason = "copy button not found";
            return Outcome.Successful(new ReplyReadResult(domText, ReplyOrigin.Dom));
        }

        var reasoning = await _page.GetNewestReasoningTextAsync(cancellationToken);
        _options.Timings?.Start("copy");
        Outcome<ReplyReadResult> read;
        try
        {
            read = await ReplyReader.ReadAsync(
                mode,
                new ReplyReadContext(domText, reasoning, ct => _page.TryCaptureCopiedReplyAsync(TimeSpan.FromSeconds(5), ct)),
                summary => _log.LogInformation("Reply copy validation: {Summary}", summary),
                cancellationToken);
        }
        finally
        {
            _options.Timings?.Stop("copy");
        }

        if (read.IsFailure)
            return read;

        LastReplyOriginValue = read.Data.Origin;
        LastReplyReason = read.Data.Origin is ReplyOrigin.Copy ? null : "copy capture failed";
        return read;
    }

    private static Outcome<string> ChatExhausted() => Outcome.Failure<string>(new OutcomeError(
        "The chat reached its length limit.", ErrorCodes.ChatExhausted, OutcomeErrorType.Failure));

    /// <summary>
    /// Syncs the DeepThink and Search toggles with the configuration on a new chat: reads each
    /// toggle's aria-pressed state and clicks when it disagrees with the option. A toggle whose
    /// selectors match nothing is logged and skipped — a changed web UI must never fail a turn.
    /// </summary>
    private async Task ApplyChatTogglesAsync(CancellationToken cancellationToken)
    {
        _options.Timings?.Start("toggles");
        try
        {
            var selectors = await SelectorsCatalog.LoadAsync(_options.WorkspaceRoot, _options.Browser.SelectorsFile, cancellationToken);
            if (selectors.IsFailure)
            {
                _log.LogWarning("Chat toggles not checked: selectors could not be loaded ({Reason}).", selectors.Error.Message);
                return;
            }

            await EnsureToggleAsync("DeepThink", selectors.Data.DeepThinkToggle, _options.Browser.EnableDeepThink, cancellationToken);
            await EnsureToggleAsync("Search", selectors.Data.SearchToggle, _options.Browser.EnableSearch, cancellationToken);
        }
        finally
        {
            _options.Timings?.Stop("toggles");
        }
    }

    private async Task EnsureToggleAsync(string name, string[] selectors, bool enabled, CancellationToken cancellationToken)
    {
        if (selectors.Length == 0)
            return;

        var state = await _page.GetToggleStateAsync(selectors, cancellationToken);
        if (state is null)
        {
            _log.LogWarning("The {Name} toggle did not match any selector; leaving it as-is.", name);
            return;
        }

        var pressed = state.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (pressed == enabled)
            return;

        if (await _page.ClickToggleAsync(selectors, cancellationToken))
            _log.LogInformation("The {Name} toggle was {State}; clicked it to {Enabled}.", name, state, enabled);
        else
            _log.LogWarning("The {Name} toggle needed changing but no click landed; leaving it as-is.", name);
    }

    /// <inheritdoc />
    public async Task<Outcome<string>> WaitForReplyAsync(CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromSeconds(_options.Browser.ReplyTimeoutSeconds);
        var deadline = DateTime.UtcNow + timeout;
        var stableReply = (string?)null;
        var firstTokenSeen = false;

        if (await _page.IsChatLimitNoticeVisibleAsync(cancellationToken))
            return ChatExhausted();

        _options.Timings?.Start("first-token");
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stillGenerating = await _page.IsStopIndicatorVisibleAsync(cancellationToken);
                var stamp = await _page.GetNewestAssistantStampAsync(cancellationToken);
                var isNewMessage = stamp is not null && stamp.IsDifferentFrom(_preSendStamp);
                var reply = isNewMessage && await _page.GetNewestAssistantHtmlAsync(cancellationToken) is { } html
                    ? _replyExtractor.ExtractFinalAnswer(html)
                    : null;

                if (!firstTokenSeen && (stillGenerating || (!string.IsNullOrWhiteSpace(reply) && isNewMessage)))
                {
                    firstTokenSeen = true;
                    _options.Timings?.Stop("first-token");
                    _options.Timings?.Start("completion");
                }

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
        }
        finally
        {
            if (firstTokenSeen)
                _options.Timings?.Stop("completion");
            else
                _options.Timings?.Stop("first-token");
        }

        // The notice may have appeared while waiting (the reply got cut mid-generation).
        if (await _page.IsChatLimitNoticeVisibleAsync(cancellationToken))
            return ChatExhausted();

        var screenshot = await CaptureDiagnosticsAsync("reply-timeout", cancellationToken);
        return Outcome.Failure<string>(new OutcomeError(
            $"No complete new reply within {_options.Browser.ReplyTimeoutSeconds}s." + DiagnosticsHint(screenshot),
            ErrorCodes.ReplyTimeout, OutcomeErrorType.Failure));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _disposeAsync();

    /// <summary>The signals observed while a send confirmation window is open.</summary>
    private sealed record SendSignals(
        bool FingerprintChanged,
        bool UserTextMatched,
        bool InputCleared,
        bool Chips,
        bool StopVisible);

    private static bool Confirmed(SendSignals s)
        => s.FingerprintChanged || s.UserTextMatched || (s.InputCleared && !s.Chips) || s.StopVisible;

    /// <summary>The first 40 characters of the sent text, whitespace-collapsed.</summary>
    private static string SentPrefixOf(string text)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 40 ? normalized : normalized[..40];
    }

    private static string NormalizeWhitespace(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>One snapshot of all confirm signals.</summary>
    private async Task<SendSignals> ObserveSendSignalsAsync(
        AssistantStamp? itemStampBefore, string sentPrefix, CancellationToken cancellationToken)
    {
        var stamp = await _page.GetNewestItemStampAsync(cancellationToken);
        var fingerprintChanged = stamp is not null && stamp.IsDifferentFrom(itemStampBefore);

        var itemText = await _page.GetNewestItemTextAsync(cancellationToken);
        var userTextMatched = false;
        if (itemText is not null && sentPrefix.Length > 0 &&
            NormalizeWhitespace(itemText).StartsWith(sentPrefix, StringComparison.Ordinal))
            userTextMatched = true;

        var inputCleared = await _page.IsInputClearedAsync(cancellationToken);
        var chips = await _page.AreAttachmentChipsVisibleAsync(cancellationToken);
        var stopVisible = await _page.IsStopIndicatorVisibleAsync(cancellationToken);

        return new SendSignals(fingerprintChanged, userTextMatched, inputCleared, chips, stopVisible);
    }

    /// <summary>
    /// A send is confirmed when ANY of these hold: the newest message item's fingerprint
    /// changed, a new user message starts with the sent text, the input is cleared with no
    /// attachment chips remaining, or the stop/generating indicator is visible. Polled at
    /// ~100 ms; the final observation after the deadline catches late flips.
    /// </summary>
    private async Task<(bool Confirmed, SendSignals Signals)> ConfirmSendAsync(
        AssistantStamp? itemStampBefore, string sentPrefix, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_options.Browser.SendConfirmSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var signals = await ObserveSendSignalsAsync(itemStampBefore, sentPrefix, cancellationToken);
            if (Confirmed(signals))
                return (true, signals);

            await Task.Delay(_pollDelayMs, cancellationToken);
        }

        var final = await ObserveSendSignalsAsync(itemStampBefore, sentPrefix, cancellationToken);
        return (Confirmed(final), final);
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

        // SetInputFiles SUCCEEDED but no upload sign: the content may be attached invisibly
        // (virtualized composer, slow chips). Pasting on top would DOUBLE the content —
        // treat the upload as done. Paste only when the upload itself failed (above).
        _log.LogWarning(
            "No sign of the uploaded files within {Seconds}s; treating the upload as done (SetInputFiles succeeded) and NOT pasting on top of it.",
            _options.Browser.UploadWaitSeconds);
        return Outcome.Successful(AttachmentDelivery.Uploaded);
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
