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
        var found = await FirstVisibleAsync(_page, _selectors.StopIndicator, timeoutMs: 400, cancellationToken);
        return found is not null;
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
                await locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 1_000 });
                var disabled = await EvaluateAsync(locator,
                    "element => element.getAttribute('aria-disabled') === 'true' || element.className.includes('ds-button--disabled')", cancellationToken);
                return disabled != "true";
            }
            catch (Exception) when (false)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }

        // No send-control selector matched: undetectable. Callers must not block on it —
        // Enter is the fallback anyway.
        return null;
    }

    /// <inheritdoc />
    public async Task<bool> AreAttachmentChipsVisibleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var selector in _selectors.AttachmentChips)
        {
            try
            {
                await _page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 300,
                });
                return true;
            }
            catch (Exception) when (false)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }

        return false;
    }

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
        for (var index = 0; index < _selectors.InputBox.Length; index++)
        {
            var selector = _selectors.InputBox[index];
            try
            {
                var input = _page.Locator(selector).First;
                await input.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                _log.LogDebug("InputBox matched selector #{Index}: {Selector}", index, selector);
                await input.FillAsync(string.Empty);
                await _page.Keyboard.InsertTextAsync(text);
                return;
            }
            catch (Exception) when (false)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }

        throw new InvalidOperationException(
            $"Input box not found; none of the selectors matched: {string.Join(", ", _selectors.InputBox)}");
    }

    /// <inheritdoc />
    public async Task<bool> TryClickSendAsync(CancellationToken cancellationToken = default)
    {
        // Pick the first visible candidate that is not aria-disabled; Enter stays the last fallback.
        cancellationToken.ThrowIfCancellationRequested();
        for (var index = 0; index < _selectors.SendButton.Length; index++)
        {
            try
            {
                var locator = _page.Locator(_selectors.SendButton[index]).First;
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 3_000,
                });

                var disabled = await EvaluateAsync(locator,
                    "element => element.getAttribute('aria-disabled') === 'true' || (element.className && String(element.className).includes('ds-button--disabled'))", cancellationToken);
                if (disabled == "true")
                {
                    _log.LogDebug("SendButton #{Index} matched but is disabled; trying next", index);
                    continue;
                }

                _log.LogDebug("SendButton matched selector #{Index}: {Selector}", index, _selectors.SendButton[index]);
                await locator.ClickAsync();
                return true;
            }
            catch (Exception) when (false)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
            catch (Exception ex)
            {
                _log.LogWarning("Send click failed on #{Index}: {Message}", index, ex.Message);
            }
        }

        _log.LogDebug("No SendButton selector matched");
        return false;
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
        var input = await FirstVisibleAsync(_page, _selectors.FileInput, timeoutMs: 5_000, cancellationToken);
        if (input is null)
            return false;

        try
        {
            await input.SetInputFilesAsync(filePaths.Select(p => p.Replace('\\', '/')).ToArray());
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning("File upload failed: {Message}", ex.Message);
            return false;
        }
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

        foreach (var selector in _selectors.MessageCopyButton)
        {
            try
            {
                await newest.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = (float)timeout.TotalMilliseconds,
                });
                _log.LogDebug("MessageCopyButton matched selector: {Selector}", selector);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }

        return false;
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

        await WithCancellationAsync(_page.EvaluateAsync(InstallInterceptionScript), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> TryCaptureCopiedReplyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Strategy 1: interception (OS clipboard untouched).
        await WithCancellationAsync(_page.EvaluateAsync("() => { window.__keylessCapture = true; window.__keylessCapturedText = null; }"), cancellationToken);
        try
        {
            var newest = await GetNewestAssistantLocatorAsync(cancellationToken);
            if (newest is not null)
            {
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

                foreach (var selector in _selectors.MessageCopyButton)
                {
                    try
                    {
                        await newest.Locator(selector).First.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (TimeoutException)
                    {
                        // Try the next fallback selector.
                    }
                }
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

            try
            {
                await newest.HoverAsync();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Hover is best-effort.
            }

            var clicked = false;
            foreach (var selector in _selectors.MessageCopyButton)
            {
                try
                {
                    await newest.Locator(selector).First.ClickAsync(new LocatorClickOptions { Timeout = 3_000 });
                    clicked = true;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    // Try the next fallback selector.
                }
            }

            if (!clicked)
                return null;

            await Task.Delay(500, cancellationToken);
            var text = await EvaluateAsync(_page.Locator("body").First,
                "async () => { try { return await navigator.clipboard.readText(); } catch { return null; } }",
                cancellationToken);
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

    internal static async Task<ILocator?> FirstVisibleAsync(IPage page, string[] selectors, int timeoutMs, CancellationToken cancellationToken = default)
    {
        foreach (var selector in selectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var locator = page.Locator(selector).First;
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs,
                });
                return locator;
            }
            catch (Exception) when (false)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }

        return null;
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
