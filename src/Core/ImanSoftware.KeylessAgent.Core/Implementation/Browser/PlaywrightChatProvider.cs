using ImanSoftware.DateTimeProvider;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Logging;
using ImanSoftware.Outcomes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Drives the user's real Chrome over CDP (default) or a persistent-context fallback.
/// Never stores or types passwords; when the login page shows up, the user logs in manually
/// while the agent waits.
/// </summary>
internal sealed class PlaywrightChatProvider : IChatProvider
{
    private const string ChatUrl = "https://chat.deepseek.com/";
    private const int LoginWaitSeconds = 300;

    private readonly LoggerAdapter<PlaywrightChatProvider> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IReplyExtractor _replyExtractor;
    private readonly DefaultDateTimeProvider _time = new();

    public PlaywrightChatProvider()
        : this(null, null)
    {
    }

    internal PlaywrightChatProvider(ILoggerFactory? loggerFactory, IReplyExtractor? replyExtractor)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = new LoggerAdapter<PlaywrightChatProvider>(_loggerFactory);
        _replyExtractor = replyExtractor ?? new ReplyExtractor();
    }

    /// <inheritdoc />
    public async Task<Outcome<IChatSession>> StartSessionAsync(ChatSessionOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var playwright = await Playwright.CreateAsync();
            var connection = await ConnectAsync(playwright, options.Browser);
            if (connection.IsFailure)
                return Outcome.Failure<IChatSession>(connection.Error);

            var (browser, context, ownsBrowser) = connection.Data;
            var pages = context.Pages;
            var page = pages.Count > 0 ? pages[pages.Count - 1] : await context.NewPageAsync();

            var selectors = SelectorsCatalog.Load(options.WorkspaceRoot, options.Browser.SelectorsFile);
            if (selectors.IsFailure)
                return Outcome.Failure<IChatSession>(selectors.Error);

            await page.GotoAsync(options.ResumeChatUrl ?? ChatUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000,
            });

            await WaitForLoginIfNeeded(page, selectors.Data);
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 10_000 });
            }
            catch (TimeoutException)
            {
                // NetworkIdle is a nicety, not a requirement; continue with the DOM as-is.
            }

            if (options.ResumeChatUrl is null)
            {
                await ClickNewChat(page, selectors.Data);
                await EnsureToggles(page, selectors.Data, options.Browser);
            }

            IChatSession session = new PlaywrightChatSession(
                page, browser, ownsBrowser, selectors.Data, options, _replyExtractor,
                new LoggerAdapter<PlaywrightChatSession>(_loggerFactory),
                _time);
            return Outcome.Successful(session);
        }
        catch (Exception ex)
        {
            return Outcome.Failure<IChatSession>(new OutcomeError(ex));
        }
    }

    private static async Task<Outcome<(IBrowser? Browser, IBrowserContext Context, bool OwnsBrowser)>> ConnectAsync(
        IPlaywright playwright, BrowserOptions options)
    {
        // --- Default: attach to the user's running Chrome (started with --remote-debugging-port) ---
        try
        {
            var browser = await playwright.Chromium.ConnectOverCDPAsync(
                $"http://localhost:{options.ChromeDebugPort}",
                new BrowserTypeConnectOverCDPOptions { Timeout = 15_000 });
            var contexts = browser.Contexts;
            var context = contexts.Count > 0 ? contexts[0] : null;
            if (context is not null)
            {
                (IBrowser? Browser, IBrowserContext Context, bool OwnsBrowser) attached = (browser, context, false);
                return Outcome.Successful(attached);
            }
        }
        catch (Exception)
        {
            // Fall through to the persistent-context fallback below.
        }

        // --- Fallback: launch a persistent profile so the login survives between runs ---
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeylessAgent", "chrome-profile");
        Directory.CreateDirectory(profile);

        var launched = await playwright.Chromium.LaunchPersistentContextAsync(
            profile,
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,
                Channel = "chrome",
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            });
        (IBrowser? Browser, IBrowserContext Context, bool OwnsBrowser) fallback = (null, launched, true);
        return Outcome.Successful(fallback);
    }

    private async Task WaitForLoginIfNeeded(IPage page, SelectorsCatalog selectors)
    {
        var loginLocator = await FirstVisible(page, selectors.LoginIndicator, timeoutMs: 5_000);
        if (loginLocator is null)
            return;

        _log.LogWarning("Login page detected; waiting up to {Seconds}s for a manual login…", LoginWaitSeconds);
        var deadline = _time.Now.ToUniversalTime().AddSeconds(LoginWaitSeconds);
        while (_time.Now.ToUniversalTime() < deadline)
        {
            await page.WaitForTimeoutAsync(2_000);
            if (await FirstVisible(page, selectors.LoginIndicator, timeoutMs: 1_000) is null)
                return;
        }

        throw new TimeoutException("The chat was still not logged in after 5 minutes.");
    }

    private static async Task ClickNewChat(IPage page, SelectorsCatalog selectors)
    {
        foreach (var selector in selectors.NewChatButton)
        {
            try
            {
                var locator = page.Locator(selector).First;
                await locator.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                await page.WaitForTimeoutAsync(500);
                return;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }
    }

    private static async Task EnsureToggles(IPage page, SelectorsCatalog selectors, BrowserOptions options)
    {
        await SetToggle(page, selectors.DeepThinkToggle, options.EnableDeepThink);
        await SetToggle(page, selectors.SearchToggle, options.EnableSearch);
    }

    private static async Task SetToggle(IPage page, string[] selectors, bool wantedOn)
    {
        foreach (var selector in selectors)
        {
            try
            {
                var toggle = page.Locator(selector).First;
                await toggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5_000 });
                var pressed = await toggle.GetAttributeAsync("aria-pressed");

                if (!string.Equals(pressed, wantedOn ? "true" : "false", StringComparison.OrdinalIgnoreCase))
                {
                    await toggle.ClickAsync();
                    await page.WaitForTimeoutAsync(300);
                }

                var verify = await toggle.GetAttributeAsync("aria-pressed");
                if (string.Equals(verify, wantedOn ? "true" : "false", StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }
        // The DOM sample does not cover every toggle state; a missing toggle is a warning, not an error.
    }

    internal static async Task<ILocator?> FirstVisible(IPage page, string[] selectors, int timeoutMs)
    {
        foreach (var selector in selectors)
        {
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
            catch (TimeoutException)
            {
                // Try the next fallback selector.
            }
        }

        return null;
    }
}
