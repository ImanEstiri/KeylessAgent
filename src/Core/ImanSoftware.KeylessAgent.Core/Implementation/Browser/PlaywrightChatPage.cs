using ImanSoftware.Logging;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// The Playwright-backed IChatPage: talks to the real chat DOM through the selector catalog,
/// preferring structural selectors over hashed class names. Playwright's own waits do not take
/// a CancellationToken, so every operation races the token — Ctrl+C interrupts promptly.
/// </summary>
internal sealed class PlaywrightChatPage : IChatPage
{
    private readonly IPage _page;
    private readonly SelectorsCatalog _selectors;
    private readonly bool _debug;
    private readonly ILoggerAdapter<PlaywrightChatPage> _log;

    public PlaywrightChatPage(IPage page, SelectorsCatalog selectors, bool debug, ILoggerAdapter<PlaywrightChatPage> log)
    {
        _page = page;
        _selectors = selectors;
        _debug = debug;
        _log = log;
    }

    /// <inheritdoc />
    public string Url => _page.Url;

    /// <inheritdoc />
    public async Task<AssistantStamp?> GetNewestAssistantStampAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var newest = await GetNewestAssistantLocatorAsync(cancellationToken);
        if (newest is null)
            return null;

        var key = await EvaluateAsync(newest,
            "element => element.closest('[data-virtual-list-item-key]')?.getAttribute('data-virtual-list-item-key') ?? element.getAttribute('data-virtual-list-item-key')", cancellationToken);
        var text = await EvaluateAsync(newest,
            "element => { const final = element.querySelector('.ds-markdown.ds-assistant-message-main-content'); return final ? final.innerText : ''; }", cancellationToken);
        var count = await CountAssistantMessagesAsync(cancellationToken);

        return new AssistantStamp(
            Key: string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
            TextHash: text is null ? 0 : StableHash(text),
            VisibleCount: count);
    }

    /// <inheritdoc />
    public async Task<string?> GetNewestAssistantHtmlAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var newest = await GetNewestAssistantLocatorAsync(cancellationToken);
        if (newest is null)
            return null;

        var html = await EvaluateAsync(newest, "element => element.outerHTML", cancellationToken);
        return string.IsNullOrEmpty(html) ? null : html;
    }

    /// <inheritdoc />
    public async Task<int> GetMessageItemCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (var index = 0; index < _selectors.MessageItems.Length; index++)
        {
            var selector = _selectors.MessageItems[index];
            try
            {
                _log.LogDebug("MessageItems matched selector #{Index}: {Selector}", index, selector);
                return (int)await WithCancellationAsync(_page.Locator(selector).CountAsync(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The virtualized list re-renders constantly; try the next fallback selector.
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public async Task<bool> IsStopIndicatorVisibleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Instant check: polling loops call this every ~100 ms, so an absent Stop button
        // must cost one round trip, never a per-selector wait.
        return await IsAnyVisibleAsync(_page, _selectors.StopIndicator, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> IsInputClearedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var selector in _selectors.InputBox)
        {
            try
            {
                var value = await EvaluateAsync(_page.Locator(selector).First, "element => element.value ?? ''", cancellationToken);
                return string.IsNullOrEmpty(value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Try the next fallback selector.
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<bool?> IsSendEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var selector in _selectors.SendButton)
        {
            try
            {
                var locator = _page.Locator(selector).First;

                // Instant probe: absent elements cost one Evaluate round trip, never a wait.
                // The disable state crosses as a JSON string so no bool/string mangling can
                // flip the result (a raw bool would ToString as "True" and compare unequal).
                // The disable-value assert is manual via `keyless doctor` (the unit tests run
                // against FakeChatPage, which returns the bool directly).
                var disabledJson = await EvaluateAsync(locator,
                    "element => JSON.stringify({ d: element.getAttribute('aria-disabled') === 'true' || String(element.className).includes('ds-button--disabled') })",
                    cancellationToken);
                if (disabledJson is null)
                    continue; // selector matched nothing; try the next fallback.

                var isDisabled = !string.IsNullOrEmpty(disabledJson) &&
                    System.Text.Json.JsonDocument.Parse(disabledJson).RootElement
                        .TryGetProperty("d", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
                return !isDisabled;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Try the next fallback selector.
            }
        }

        // No send-control selector matched: undetectable. Callers must not block on it —
        // Enter is the fallback anyway.
        return null;
    }

    /// <inheritdoc />
    public async Task<string?> GetToggleStateAsync(string[] selectors, CancellationToken cancellationToken = default)
    {
        foreach (var selector in selectors)
        {
            // Instant probe: one round trip per fallback, no waiting.
            var state = await EvaluateAsync(_page.Locator(selector).First,
                "element => element.getAttribute('aria-pressed')", cancellationToken);
            if (state is not null)
                return state;
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> ClickToggleAsync(string[] selectors, CancellationToken cancellationToken = default)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var locator = _page.Locator(selector).First;
                await locator.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Try the next fallback selector.
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<bool> IsChatLimitNoticeVisibleAsync(CancellationToken cancellationToken = default)
        => await IsAnyVisibleAsync(_page, _selectors.ChatLimitNotice, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> IsAnyVisibleAsync(string[] selectors, CancellationToken cancellationToken = default)
        => await IsAnyVisibleAsync(_page, selectors, cancellationToken);

    /// <inheritdoc />
    public async Task<int?> WaitForAnyVisibleAsync(string[] selectors, int timeoutMs, CancellationToken cancellationToken = default)
    {
        var winner = await FirstVisibleAsync(_page, selectors, timeoutMs, cancellationToken);
        return winner?.Index;
    }

    private static async Task<string> GetTargetIdAsync(ICDPSession session)
    {
        var response = await session.SendAsync("Target.getTargetInfo");
        var element = (System.Text.Json.JsonElement)response;
        return element.GetProperty("targetInfo").GetProperty("targetId").GetString() ?? string.Empty;
    }

    /// <inheritdoc />
    public async Task FocusTabAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WithCancellationAsync(_page.BringToFrontAsync(), cancellationToken);
            var focusSession = await WithCancellationAsync(_page.Context.NewCDPSessionAsync(_page), cancellationToken);
            await focusSession.SendAsync("Target.activateTarget",
                new Dictionary<string, object> { ["targetId"] = await GetTargetIdAsync(focusSession) });
            await focusSession.DetachAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Focus is best-effort.
        }
    }

    /// <inheritdoc />
    public async Task<bool> AreAttachmentChipsVisibleAsync(CancellationToken cancellationToken = default)
        => await IsAnyVisibleAsync(_page, _selectors.AttachmentChips, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetInputFileNamesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var selector in _selectors.FileInput)
        {
            try
            {
                var raw = await EvaluateAsync(_page.Locator(selector).First,
                    "element => Array.from(element.files || []).map(f => f.name).join('|')", cancellationToken);
                if (string.IsNullOrEmpty(raw))
                    continue;

                return raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Try the next fallback selector.
            }
        }

        return [];
    }

    /// <inheritdoc />
    public async Task<bool> IsFileNameVisibleAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Chips render the file name next to the composer; a page-wide text probe is
            // enough as a positive signal (the composer area is where uploads render).
            var count = await WithCancellationAsync(_page.GetByText(fileName).CountAsync(), cancellationToken);
            return count > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task InsertTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ONE parallel wait over the whole fallback list: an absent input costs the 10s
        // upper bound once, never once per fallback selector.
        var match = await FirstVisibleAsync(_page, _selectors.InputBox, 10_000, cancellationToken);
        if (match is null)
        {
            throw new InvalidOperationException(
                $"Input box not found; none of the selectors matched: {string.Join(", ", _selectors.InputBox)}");
        }

        var (input, index) = match.Value;
        _log.LogDebug("InputBox matched selector #{Index}: {Selector}", index, _selectors.InputBox[index]);
        await input.FillAsync(string.Empty);
        await _page.Keyboard.InsertTextAsync(text);
    }

    /// <inheritdoc />
    public async Task<bool> TryClickSendAsync(CancellationToken cancellationToken = default)
    {
        // Pick the first visible candidate that is not aria-disabled; Enter stays the last fallback.
        cancellationToken.ThrowIfCancellationRequested();

        // ONE parallel wait over the whole fallback list (3s upper bound once, not per
        // fallback); the per-candidate disabled/paperclip screen stays sequential because
        // it must skip non-sending candidates in fallback order.
        var match = await FirstVisibleAsync(_page, _selectors.SendButton, 3_000, cancellationToken);
        if (match is null)
        {
            _log.LogDebug("No SendButton selector matched");
            return false;
        }

        var (locator, index) = match.Value;
        try
        {
            var state = await EvaluateAsync(locator,
                "element => JSON.stringify({ dis: element.getAttribute('aria-disabled') === 'true' || String(element.className).includes('ds-button--disabled'), paperclip: String(element.className).includes('ds-button--iconLabelPrimary') })",
                cancellationToken);
            if (!string.IsNullOrEmpty(state))
            {
                var parsed = System.Text.Json.JsonDocument.Parse(state);
                var dis = parsed.RootElement.TryGetProperty("dis", out var disValue) && disValue.ValueKind == System.Text.Json.JsonValueKind.True;
                var paperclip = parsed.RootElement.TryGetProperty("paperclip", out var paperclipValue) && paperclipValue.ValueKind == System.Text.Json.JsonValueKind.True;

                // Disabled buttons can never send...
                if (dis)
                {
                    _log.LogDebug("SendButton #{Index} matched but is disabled; Enter is the fallback", index);
                    return false;
                }

                // ...and the iconLabelPrimary button next to the file input is the PAPERCLIP:
                // clicking it opens the OS file dialog ("Open" window) instead of sending.
                if (paperclip)
                {
                    _log.LogDebug("SendButton #{Index} matched the paperclip (file attach); Enter is the fallback", index);
                    return false;
                }
            }

            _log.LogDebug("SendButton matched selector #{Index}: {Selector}", index, _selectors.SendButton[index]);
            await locator.ClickAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Send click failed on #{Index}: {Message}", index, ex.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task PressEnterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.Keyboard.PressAsync("Enter");
    }

    /// <inheritdoc />
    public async Task<bool> TryUploadFilesAsync(IReadOnlyList<string> filePaths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The chat's file input is display:none — waiting for VISIBLE always times out and made
        // every upload fall back to pasting. SetInputFiles works on hidden inputs; each fallback
        // selector is tried directly.
        foreach (var selector in _selectors.FileInput)
        {
            try
            {
                var locator = _page.Locator(selector).First;
                await locator.SetInputFilesAsync(filePaths.Select(p => p.Replace('\\', '/')).ToArray());
                cancellationToken.ThrowIfCancellationRequested();
                _log.LogDebug("FileInput matched selector: {Selector}", selector);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug("FileInput {Selector} failed: {Message}", selector, ex.Message);
            }
        }

        _log.LogWarning("No FileInput selector accepted the upload");
        return false;
    }

    /// <inheritdoc />
    public async Task<string?> GetNewestReasoningTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var newest = await GetNewestAssistantLocatorAsync(cancellationToken);
        if (newest is null)
            return null;

        return await EvaluateAsync(newest,
            "element => { const think = element.querySelector('.ds-think-content'); return think ? think.innerText : ''; }",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> WaitForMessageCopyButtonAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var newest = await GetNewestAssistantLocatorAsync(cancellationToken);
        if (newest is null)
            return false;

        try
        {
            await newest.HoverAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Hover before copy failed: {Message}", ex.Message);
        }

        // ONE parallel wait over the whole fallback list, scoped to the newest message: the
        // timeout is the caller's upper bound once, never once per fallback selector.
        var match = await FirstVisibleAsync(newest, _selectors.MessageCopyButton, (int)timeout.TotalMilliseconds, cancellationToken);
        if (match is null)
            return false;

        _log.LogDebug("MessageCopyButton matched selector #{Index}: {Selector}", match.Value.Index, _selectors.MessageCopyButton[match.Value.Index]);
        return true;
    }

    /// <summary>
    /// Clicks the newest message's Copy control: ONE visible-filtered Or-wait over the whole
    /// fallback list (the 3s-per-fallback total upper bound is spent once, never once per
    /// selector), then clicks the matched element. Returns false when no fallback is visible
    /// or the click did not land.
    /// </summary>
    private async Task<bool> ClickNewestCopyControlAsync(ILocator newest, CancellationToken cancellationToken)
    {
        var totalMs = 3_000 * _selectors.MessageCopyButton.Length;
        var match = await FirstVisibleAsync(newest, _selectors.MessageCopyButton, totalMs, cancellationToken);
        if (match is null)
            return false;

        _log.LogDebug("MessageCopyButton matched selector #{Index}: {Selector}", match.Value.Index, _selectors.MessageCopyButton[match.Value.Index]);
        try
        {
            await match.Value.Locator.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Copy click failed on #{Index}: {Message}", match.Value.Index, ex.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task PrepareReplyCaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _page.ExposeBindingAsync<string, string?>("__keylessReportCopy", (_, text) =>
        {
            if (!string.IsNullOrEmpty(text))
                _capturedCopy = text;
            return text;
        });

        // Defensive: a fresh capture window must never see the previous reply's copied text.
        _capturedCopy = null;
        await WithCancellationAsync(_page.EvaluateAsync(InstallInterceptionScript), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> TryCaptureCopiedReplyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A stale capture would return the PREVIOUS reply; the capture window starts empty.
        _capturedCopy = null;

        // Strategy 1: interception (OS clipboard untouched).
        await WithCancellationAsync(_page.EvaluateAsync("() => { window.__keylessCapture = true; window.__keylessCapturedText = null; }"), cancellationToken);
        try
        {
            var newest = await GetNewestAssistantLocatorAsync(cancellationToken);
            if (newest is not null)
            {
                await ClickNewestCopyControlAsync(newest, cancellationToken);
            }

            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(_capturedCopy))
                    return _capturedCopy;

                var polled = await EvaluateAsync(_page.Locator("body").First,
                    "() => window.__keylessCapturedText", cancellationToken);
                if (!string.IsNullOrEmpty(polled))
                    return polled;

                await Task.Delay(200, cancellationToken);
            }
        }
        finally
        {
            try
            {
                await _page.EvaluateAsync("() => { window.__keylessCapture = false; }");
            }
            catch
            {
                // Disabling the flag is best-effort.
            }
        }

        // Strategy 2: real OS clipboard, save/restore in finally.
        string? previous = null;
        try
        {
            await _page.Context.GrantPermissionsAsync(new[] { "clipboard-read", "clipboard-write" });
            var previousRaw = await EvaluateAsync(_page.Locator("body").First,
                "async () => { try { return await navigator.clipboard.readText(); } catch { return null; } }",
                cancellationToken);
            previous = string.IsNullOrEmpty(previousRaw) ? null : previousRaw;

            var newest = await GetNewestAssistantLocatorAsync(cancellationToken);
            if (newest is null)
                return null;

            await ClickNewestCopyControlAsync(newest, cancellationToken);

            // Poll the clipboard briefly instead of one fixed 500 ms sleep: the Copy click
            // usually lands within one or two polls, and the total budget (500 ms) is unchanged.
            string? text = null;
            var clipboardDeadline = DateTime.UtcNow.AddMilliseconds(500);
            while (string.IsNullOrEmpty(text) && DateTime.UtcNow < clipboardDeadline)
            {
                text = await EvaluateAsync(_page.Locator("body").First,
                    "async () => { try { return await navigator.clipboard.readText(); } catch { return null; } }",
                    cancellationToken);
                if (string.IsNullOrEmpty(text) && DateTime.UtcNow < clipboardDeadline)
                    await Task.Delay(75, cancellationToken);
            }

            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            // The user's clipboard is ALWAYS restored.
            if (previous is not null)
            {
                try
                {
                    await _page.EvaluateAsync("async (text) => { await navigator.clipboard.writeText(text); }", previous);
                }
                catch
                {
                    // Restore is best-effort.
                }
            }
        }
    }

    private const string InstallInterceptionScript = """
        (() => {
            window.__keylessCapture = false;
            window.__keylessCapturedText = null;
            const report = (text) => {
                if (!window.__keylessCapture || !text) return;
                window.__keylessCapturedText = text;
                window.__keylessReportCopy(text);
            };
            if (navigator.clipboard && navigator.clipboard.writeText) {
                const original = navigator.clipboard.writeText.bind(navigator.clipboard);
                navigator.clipboard.writeText = async (text) => {
                    report(text);
                    if (!window.__keylessCapture) return original(text);
                };
            }
            const originalExec = document.execCommand.bind(document);
            document.execCommand = function (cmd, ...rest) {
                if (String(cmd).toLowerCase() === 'copy') {
                    const sel = document.getSelection ? String(document.getSelection()) : '';
                    report(sel);
                    if (!window.__keylessCapture) return originalExec(cmd, ...rest);
                    return true;
                }
                return originalExec(cmd, ...rest);
            };
            document.addEventListener('copy', (event) => {
                if (!window.__keylessCapture) return;
                const text = event.clipboardData ? event.clipboardData.getData('text/plain') : '';
                report(text);
                event.preventDefault();
            }, true);
        })()
        """;

    private string? _capturedCopy;

    /// <inheritdoc />
    public async Task SaveDebugScreenshotAsync(string fullPath, CancellationToken cancellationToken = default)

    {
        if (!_debug)
            return;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await _page.ScreenshotAsync(new PageScreenshotOptions { Path = fullPath, FullPage = false });
            _log.LogInformation("Debug screenshot saved: {Path}", fullPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Screenshots are best-effort.
        }
    }

    private async Task<ILocator?> GetNewestAssistantLocatorAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < _selectors.AssistantMessage.Length; index++)
        {
            var selector = _selectors.AssistantMessage[index];
            try
            {
                var count = (int)await WithCancellationAsync(_page.Locator(selector).CountAsync(), cancellationToken);
                if (count == 0)
                    continue;

                _log.LogDebug("AssistantMessage matched selector #{Index}: {Selector}", index, selector);
                return _page.Locator(selector).Nth(count - 1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The virtualized list re-renders constantly; try the next fallback selector.
            }
        }

        return null;
    }

    private async Task<int> CountAssistantMessagesAsync(CancellationToken cancellationToken)
    {
        foreach (var selector in _selectors.AssistantMessage)
        {
            try
            {
                return (int)await WithCancellationAsync(_page.Locator(selector).CountAsync(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Try the next fallback selector.
            }
        }

        return 0;
    }

    private async Task<string?> EvaluateAsync(ILocator locator, string script, CancellationToken cancellationToken)
    {
        try
        {
            var result = await WithCancellationAsync(locator.EvaluateAsync(script), cancellationToken);
            return result?.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for the FIRST VISIBLE match of any fallback selector — all selectors race in ONE
    /// combined Or-locator with ONE timeout, so an absent element costs the timeout once, not
    /// once per fallback. Returns the winning locator and the matched fallback index.
    /// </summary>
    internal static async Task<(ILocator Locator, int Index)?> FirstVisibleAsync(
        IPage page, string[] selectors, int timeoutMs, CancellationToken cancellationToken = default)
        => await FirstVisibleAsync(selector => page.Locator(selector), selectors, timeoutMs, cancellationToken);

    /// <summary>Scope-based variant: the fallbacks race within a parent element.</summary>
    internal static async Task<(ILocator Locator, int Index)?> FirstVisibleAsync(
        ILocator scope, string[] selectors, int timeoutMs, CancellationToken cancellationToken = default)
        => await FirstVisibleAsync(selector => scope.Locator(selector), selectors, timeoutMs, cancellationToken);

    /// <param name="resolve">Builds a locator for one selector at the wait's root scope.</param>
    private static async Task<(ILocator Locator, int Index)?> FirstVisibleAsync(
        Func<string, ILocator> resolve, string[] selectors, int timeoutMs, CancellationToken cancellationToken)
    {
        if (selectors.Length == 0)
            return null;

        cancellationToken.ThrowIfCancellationRequested();

        // Filter(Visible) matters: without it, combined.First is the first match in DOM
        // ORDER — a hidden element listed first would make the visible wait time out even
        // though a later fallback is visible. With the filter, the race is over VISIBLE
        // matches only, and the winner selection below agrees with it.
        var combined = resolve(selectors[0]);
        for (var index = 1; index < selectors.Length; index++)
            combined = combined.Or(resolve(selectors[index]));
        combined = combined.Filter(new LocatorFilterOptions { Visible = true });

        try
        {
            await combined.First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeoutMs,
            });
        }
        catch (TimeoutException)
        {
            return null;
        }

        // The combined wait fired: identify the winner with instant (no-wait) visibility
        // probes resolved at the SAME scope the race ran at (a nested probe would search
        // inside the already-matched element and find nothing).
        var winnerIndex = await SelectVisibleWinnerAsync(
            selectors, async selector => await resolve(selector).First.IsVisibleAsync(), cancellationToken);
        if (winnerIndex is null)
            return null;

        return (resolve(selectors[winnerIndex.Value]).First, winnerIndex.Value);
    }

    /// <summary>
    /// The winner rule of the visibility race: the FIRST selector (in fallback order) whose
    /// element is visible right now. A hidden earlier match never hides a later visible one —
    /// null when nothing is visible (the element vanished between wait and probe).
    /// </summary>
    internal static async Task<int?> SelectVisibleWinnerAsync(
        string[] selectors, Func<string, Task<bool>> isVisible, CancellationToken cancellationToken = default)
    {
        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await isVisible(selector))
                return Array.IndexOf(selectors, selector);
        }

        return null;
    }

    /// <summary>
    /// Instant presence+visibility check across the fallback list in ONE round trip
    /// (an Or-locator's IsVisibleAsync never waits) — for "is it there right now" questions:
    /// polling loops, login probes, notices, toggles.
    /// </summary>
    internal static async Task<bool> IsAnyVisibleAsync(IPage page, string[] selectors, CancellationToken cancellationToken = default)
    {
        if (selectors.Length == 0)
            return false;

        cancellationToken.ThrowIfCancellationRequested();

        var combined = page.Locator(selectors[0]);
        for (var index = 1; index < selectors.Length; index++)
            combined = combined.Or(page.Locator(selectors[index]));

        try
        {
            return await combined.First.IsVisibleAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Non-generic task variant of the cancellation race.</summary>
    private static async Task WithCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        await WithCancellationAsync(
            task.ContinueWith(_ => true, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default),
            cancellationToken);
    }

    /// <summary>
    /// Playwright's waits carry their own timeouts but no CancellationToken; racing the token
    /// makes Ctrl+C interrupt any in-flight wait promptly.
    /// </summary>
    private static async Task<T> WithCancellationAsync<T>(Task<T> task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(() => cancellation.TrySetResult(true));
        var completed = await Task.WhenAny(task, cancellation.Task);
        if (completed != task)
            throw new OperationCanceledException(cancellationToken);

        return await task;
    }

    internal static int StableHash(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in text)
                hash = (hash * 31) + c;
            return hash;
        }
    }
}
