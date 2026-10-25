using ImanSoftware.Logging;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// The Playwright-backed IChatPage: talks to the real chat DOM through the selector catalog,
/// preferring structural selectors over hashed class names.
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
    public AssistantStamp? GetNewestAssistantStamp()
    {
        var newest = GetNewestAssistantLocator();
        if (newest is null)
            return null;

        var key = Evaluate(newest,
            "element => element.closest('[data-virtual-list-item-key]')?.getAttribute('data-virtual-list-item-key') ?? element.getAttribute('data-virtual-list-item-key')");
        var text = Evaluate(newest,
            "element => { const final = element.querySelector('.ds-markdown.ds-assistant-message-main-content'); return final ? final.innerText : ''; }");
        var count = CountAssistantMessages();

        return new AssistantStamp(
            Key: string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
            TextHash: text is null ? 0 : StableHash(text),
            VisibleCount: count);
    }

    /// <inheritdoc />
    public string? GetNewestAssistantHtml()
    {
        var newest = GetNewestAssistantLocator();
        if (newest is null)
            return null;

        var html = Evaluate(newest, "element => element.outerHTML");
        return string.IsNullOrEmpty(html) ? null : html;
    }

    /// <inheritdoc />
    public int GetMessageItemCount()
    {
        foreach (var selector in _selectors.MessageItems)
        {
            try
            {
                return _page.Locator(selector).CountAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // The virtualized list re-renders constantly; try the next fallback selector.
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public bool IsStopIndicatorVisible() => FirstVisible(_page, _selectors.StopIndicator, timeoutMs: 400) is not null;

    /// <inheritdoc />
    public bool IsInputCleared()
    {
        foreach (var selector in _selectors.InputBox)
        {
            try
            {
                var value = Evaluate(_page.Locator(selector).First, "element => element.value ?? ''");
                return string.IsNullOrEmpty(value);
            }
            catch
            {
                // Try the next fallback selector.
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsSendEnabled()
    {
        foreach (var selector in _selectors.SendButton)
        {
            try
            {
                var locator = _page.Locator(selector).First;
                locator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 1_000 })
                    .GetAwaiter().GetResult();
                var disabled = Evaluate(locator,
                    "element => element.getAttribute('aria-disabled') === 'true' || element.className.includes('ds-button--disabled')");
                return disabled != "true";
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }

        // When the button cannot be inspected, assume enabled — Enter is the fallback anyway.
        return true;
    }

    /// <inheritdoc />
    public bool AreAttachmentChipsVisible()
    {
        foreach (var selector in _selectors.AttachmentChips)
        {
            try
            {
                _page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 300,
                }).GetAwaiter().GetResult();
                return true;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void InsertText(string text)
    {
        foreach (var selector in _selectors.InputBox)
        {
            try
            {
                var input = _page.Locator(selector).First;
                input.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 })
                    .GetAwaiter().GetResult();
                input.FillAsync(string.Empty).GetAwaiter().GetResult();
                _page.Keyboard.InsertTextAsync(text).GetAwaiter().GetResult();
                return;
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
    public bool TryClickSend()
    {
        var send = FirstVisible(_page, _selectors.SendButton, timeoutMs: 3_000);
        if (send is null)
            return false;

        try
        {
            send.ClickAsync().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Send click failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public void PressEnter() => _page.Keyboard.PressAsync("Enter").GetAwaiter().GetResult();

    /// <inheritdoc />
    public bool TryUploadFiles(IReadOnlyList<string> filePaths)
    {
        var input = FirstVisible(_page, _selectors.FileInput, timeoutMs: 5_000);
        if (input is null)
            return false;

        try
        {
            input.SetInputFilesAsync(filePaths.Select(p => p.Replace('\\', '/')).ToArray()).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("File upload failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public void SaveDebugScreenshot(string name)
    {
        if (!_debug)
            return;

        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "keylessagent-screens");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            _page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = false }).GetAwaiter().GetResult();
            _log.LogInformation("Debug screenshot saved: {Path}", path);
        }
        catch
        {
            // Screenshots are best-effort.
        }
    }

    private ILocator? GetNewestAssistantLocator()
    {
        foreach (var selector in _selectors.AssistantMessage)
        {
            try
            {
                var count = _page.Locator(selector).CountAsync().GetAwaiter().GetResult();
                if (count == 0)
                    continue;

                return _page.Locator(selector).Nth(count - 1);
            }
            catch
            {
                // The virtualized list re-renders constantly; try the next fallback selector.
            }
        }

        return null;
    }

    private int CountAssistantMessages()
    {
        foreach (var selector in _selectors.AssistantMessage)
        {
            try
            {
                return _page.Locator(selector).CountAsync().GetAwaiter().GetResult();
            }
            catch
            {
                // Try the next fallback selector.
            }
        }

        return 0;
    }

    private static string? Evaluate(ILocator locator, string script)
    {
        try
        {
            var result = locator.EvaluateAsync(script).GetAwaiter().GetResult();
            return result?.ToString();
        }
        catch
        {
            return null;
        }
    }

    internal static ILocator? FirstVisible(IPage page, string[] selectors, int timeoutMs)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var locator = page.Locator(selector).First;
                locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = timeoutMs,
                }).GetAwaiter().GetResult();
                return locator;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }

        return null;
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
