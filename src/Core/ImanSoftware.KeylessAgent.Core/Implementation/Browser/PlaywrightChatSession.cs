using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Logging;
using ImanSoftware.Outcomes;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// One live chat in the user's Chrome: rate-limited sends with attachment upload
/// (paste fallback) and reply waiting that reads only the final-answer element.
/// </summary>
internal sealed class PlaywrightChatSession : IChatSession
{
    private readonly IPage _page;
    private readonly IBrowser? _browser;
    private readonly bool _ownsBrowser;
    private readonly SelectorsCatalog _selectors;
    private readonly ChatSessionOptions _options;
    private readonly IReplyExtractor _replyExtractor;
    private readonly ILoggerAdapter<PlaywrightChatSession> _log;
    private readonly RateLimiter _rateLimiter;
    private readonly IFileStorage _storage = ImanFileStorageFactory.CreateStorage();
    private readonly Random _random = new();

    public PlaywrightChatSession(
        IPage page,
        IBrowser? browser,
        bool ownsBrowser,
        SelectorsCatalog selectors,
        ChatSessionOptions options,
        IReplyExtractor replyExtractor,
        ILoggerAdapter<PlaywrightChatSession> log)
    {
        _page = page;
        _browser = browser;
        _ownsBrowser = ownsBrowser;
        _selectors = selectors;
        _options = options;
        _replyExtractor = replyExtractor;
        _log = log;
        _rateLimiter = new RateLimiter(
            options.Browser.MaxMessagesPerMinute,
            options.Browser.MinDelayMs,
            options.Browser.MaxDelayMs,
            _random);
    }

    /// <inheritdoc />
    public string ChatUrl => _page.Url;

    /// <inheritdoc />
    public async Task<Outcome> SendMessageAsync(string message, IReadOnlyList<SnapshotAttachment> attachments, CancellationToken cancellationToken = default)
    {
        try
        {
            await _rateLimiter.WaitAsync(cancellationToken);

            var text = message;
            var uploadOutcome = await TryUploadAttachmentsAsync(attachments);
            if (uploadOutcome.IsFailure)
            {
                _log.LogWarning("Attachment upload failed ({Error}); pasting content into the message.", uploadOutcome.Error.Message);
                text = message + "\n\n" + string.Join("\n\n", attachments.Select(a =>
                    $"--- attached: {a.Name} ---\n{a.Content}"));
            }

            var input = await PlaywrightChatProvider.FirstVisible(_page, _selectors.InputBox, timeoutMs: 15_000)
                ?? throw new InvalidOperationException(
                    $"Input box not found; none of the selectors matched: {string.Join(", ", _selectors.InputBox)}");

            // Fill inserts the whole text at once — never per-character typing.
            await input.FillAsync(string.Empty);
            await _page.Keyboard.InsertTextAsync(text);
            await Task.Delay(_random.Next(_options.Browser.MinDelayMs, Math.Max(_options.Browser.MinDelayMs + 1, _options.Browser.MaxDelayMs)), cancellationToken);

            var send = await PlaywrightChatProvider.FirstVisible(_page, _selectors.SendButton, timeoutMs: 5_000);
            if (send is not null)
            {
                await send.ClickAsync();
            }
            else
            {
                _log.LogInformation("Send button not found; pressing Enter.");
                await _page.Keyboard.PressAsync("Enter");
            }

            return Outcome.Successful();
        }
        catch (Exception ex)
        {
            await SaveDebugScreenshotAsync("send-failed");
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

            var stillGenerating = await PlaywrightChatProvider.FirstVisible(_page, _selectors.StopIndicator, timeoutMs: 1_000) is not null;

            var newest = await GetNewestAssistantHtmlAsync();
            var reply = newest is null ? null : _replyExtractor.ExtractFinalAnswer(newest);

            if (!stillGenerating &&
                !string.IsNullOrWhiteSpace(reply) &&
                reply == stableReply)
            {
                return Outcome.Successful(reply);
            }

            if (!string.IsNullOrWhiteSpace(reply))
                stableReply = reply;
            else
                stableReply = null;

            await Task.Delay(1_000, cancellationToken);
        }

        await SaveDebugScreenshotAsync("reply-timeout");
        return Outcome.Failure<string>(new OutcomeError(
            $"No complete reply within {_options.Browser.ReplyTimeoutSeconds}s.",
            "KA_REPLY_TIMEOUT", OutcomeErrorType.Failure));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ownsBrowser)
                await _page.Context.CloseAsync();
            else if (_browser is not null)
                await _browser.CloseAsync();
        }
        catch
        {
            // Closing the browser must never crash the agent.
        }
    }

    private async Task<Outcome> TryUploadAttachmentsAsync(IReadOnlyList<SnapshotAttachment> attachments)
    {
        if (attachments.Count == 0)
            return Outcome.Successful();

        var fileInput = await PlaywrightChatProvider.FirstVisible(_page, _selectors.FileInput, timeoutMs: 5_000);
        if (fileInput is null)
        {
            return Outcome.Failure(new OutcomeError(
                "File input not found.", "KA_ATTACHMENT_UPLOAD_FAILED", OutcomeErrorType.Failure));
        }

        var directory = Path.Combine(_options.WorkspaceRoot, ".agent", "attachments");
        var created = new List<string>();
        foreach (var attachment in attachments)
        {
            var path = Path.Combine(directory, attachment.Name);
            var mkdir = await _storage.CreateDirectoryAsync(directory);
            if (mkdir.IsFailure)
                return mkdir;

            var write = await _storage.WriteAllTextAsync(path, attachment.Content);
            if (write.IsFailure)
                return write;

            created.Add(path);
        }

        try
        {
            await fileInput.SetInputFilesAsync(created.Select(p => p.Replace('\\', '/')).ToArray());
            await _page.WaitForTimeoutAsync(1_000);
            return Outcome.Successful();
        }
        catch (Exception ex)
        {
            return Outcome.Failure(new OutcomeError(ex));
        }
    }

    /// <summary>
    /// The message list is virtualized: only the newest messages exist in the DOM,
    /// so the newest assistant message is the last matching element.
    /// </summary>
    private async Task<string?> GetNewestAssistantHtmlAsync()
    {
        foreach (var selector in _selectors.AssistantMessage)
        {
            try
            {
                var count = await _page.Locator(selector).CountAsync();
                if (count == 0)
                    continue;

                var html = await _page.Locator(selector).Nth(count - 1).EvaluateAsync(
                    "element => element.outerHTML");
                return html?.ToString();
            }
            catch
            {
                // The list re-renders constantly; a miss here just means "try again next second".
            }
        }

        return null;
    }

    private async Task SaveDebugScreenshotAsync(string name)
    {
        if (!_options.Browser.Debug)
            return;

        try
        {
            var directory = Path.Combine(_options.WorkspaceRoot, ".agent", "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await _page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = false });
            _log.LogInformation("Debug screenshot saved: {Path}", path);
        }
        catch
        {
            // Screenshots are best-effort.
        }
    }
}
