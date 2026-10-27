using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.DateTimeProvider;
using ImanSoftware.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ImanSoftware.Outcomes;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Drives one live chat over the connection established by <see cref="IBrowserConnector"/>.
/// The tab picking/focusing/connection reporting lives in the connector; this provider wires
/// the page into a chat session and performs the manual-login wait (bringing the tab to front).
/// </summary>
internal sealed class PlaywrightChatProvider : IChatProvider
{
    private const int LoginWaitSeconds = 300;

    private readonly IBrowserConnector _connector;
    private readonly IDateTimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IReplyExtractor _replyExtractor;

    public PlaywrightChatProvider()
        : this(null, null)
    {
    }

    internal PlaywrightChatProvider(ILoggerFactory? loggerFactory, IReplyExtractor? replyExtractor)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _connector = new BrowserConnector();
        _time = new DefaultDateTimeProvider();
        _replyExtractor = replyExtractor ?? new ReplyExtractor();
    }

    /// <inheritdoc />
    public async Task<Outcome<IChatSession>> StartSessionAsync(ChatSessionOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            var connected = await _connector.ConnectAsync(options.Browser, new ProgressOnlyUi(), cancellationToken);
            if (connected.IsFailure)
                return Outcome.Failure<IChatSession>(connected.Error);

            var (attachment, rawPage) = connected.Data;
            var page = (IPage)rawPage;

            // Resume: when a saved chat URL exists and the current tab is not already on it,
            // navigate the tab there so the conversation continues in the same chat.
            if (!string.IsNullOrEmpty(options.ResumeChatUrl) &&
                !page.Url.StartsWith(options.ResumeChatUrl, StringComparison.OrdinalIgnoreCase))
            {
                await page.GotoAsync(options.ResumeChatUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60_000,
                });
            }

            var selectors = await SelectorsCatalog.LoadAsync(options.WorkspaceRoot, options.Browser.SelectorsFile, cancellationToken);
            if (selectors.IsFailure)
                return Outcome.Failure<IChatSession>(selectors.Error);

            await WaitForLoginIfNeeded(page, selectors.Data, options.Browser, minimizeAfterLogin: true, cancellationToken);

            IChatPage chatPage = new PlaywrightChatPage(
                page, selectors.Data, options.Browser.Debug,
                new LoggerAdapter<PlaywrightChatPage>(_loggerFactory));
            IChatSession session = new PlaywrightChatSession(
                chatPage,
                () => DisposeAsync(page, options.Browser),
                options,
                _replyExtractor,
                new RateLimiter(
                    options.Browser.MaxMessagesPerMinute,
                    options.Browser.MinDelayMs,
                    options.Browser.MaxDelayMs,
                    _time),
                new LoggerAdapter<PlaywrightChatSession>(_loggerFactory),
                ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage());
            return Outcome.Successful(session);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Outcome.Failure<IChatSession>(new OutcomeError(ex));
        }
    }

    /// <summary>
    /// Brings the tab to front and waits until the login indicator disappears
    /// (the user logs in manually; the agent continues automatically).
    /// </summary>
    internal async Task WaitForLoginIfNeeded(
        IPage page, SelectorsCatalog selectors, BrowserOptions browserOptions,
        bool minimizeAfterLogin = false, CancellationToken cancellationToken = default)
    {
        var loginLocator = await PlaywrightChatPage.FirstVisibleAsync(page, selectors.LoginIndicator, 5_000, cancellationToken);
        if (loginLocator is null)
        {
            // Already logged in: get the window out of the user's way.
            if (minimizeAfterLogin)
                await MinimizeWindowAsync(page, cancellationToken);
            return;
        }

        // Focus the tab so the user sees the login page.
        try
        {
            await page.BringToFrontAsync();
            var focusSession = await page.Context.NewCDPSessionAsync(page);
            await focusSession.SendAsync("Target.activateTarget",
                new Dictionary<string, object> { ["targetId"] = await GetTargetIdAsync(focusSession) });
            await focusSession.DetachAsync();
        }
        catch
        {
            // Focus is best-effort.
        }

        var logger = new LoggerAdapter<PlaywrightChatProvider>(NullLoggerFactory.Instance);
        logger.LogWarning("Login page detected; waiting up to {Seconds}s for a manual login…", LoginWaitSeconds);
        var deadline = DateTime.UtcNow.AddSeconds(LoginWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await page.WaitForTimeoutAsync(2_000);
            if (await PlaywrightChatPage.FirstVisibleAsync(page, selectors.LoginIndicator, 1_000, cancellationToken) is null)
            {
                // Login done: get the window out of the user's way.
                if (minimizeAfterLogin)
                    await MinimizeWindowAsync(page, cancellationToken);
                return;
            }
        }

        throw new TimeoutException("The chat was still not logged in after 5 minutes.");
    }

    /// <summary>
    /// Minimizes the browser window via CDP (Browser.setWindowBounds) so a successful launch
    /// does not steal the user's focus. Best-effort.
    /// </summary>
    internal static async Task MinimizeWindowAsync(IPage page, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await page.Context.NewCDPSessionAsync(page);
            var window = await session.SendAsync("Browser.getWindowForTarget");
            var windowId = ((System.Text.Json.JsonElement)window).GetProperty("windowId").GetInt64();
            await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object> { ["windowState"] = "minimized" },
            });
            await session.DetachAsync();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Minimizing is best-effort.
        }
    }

    /// <summary>
    /// Dispose policy: the chat tab STAYS OPEN by default — the conversation continues across
    /// turns and across runs (the next run reuses the tab and the saved chat URL). Only
    /// Browser.CloseChromeOnExit closes the window/context when the session ends.
    /// </summary>
    private static async ValueTask DisposeAsync(IPage page, BrowserOptions browserOptions)
    {
        if (!browserOptions.CloseChromeOnExit)
            return;

        try
        {
            var pages = page.Context.Pages;
            if (pages.Count == 0)
                await page.Context.CloseAsync();
        }
        catch
        {
            // Closing must never crash the agent.
        }
    }

    private static async Task<string> GetTargetIdAsync(ICDPSession session)
    {
        var response = await session.SendAsync("Target.getTargetInfo");
        var element = (System.Text.Json.JsonElement)response;
        return element.GetProperty("targetInfo").GetProperty("targetId").GetString() ?? string.Empty;
    }

    /// <summary>
    /// Connector status messages when no turn UI exists yet (the provider runs before the
    /// loop): progress lines only, no prompts.
    /// </summary>
    private sealed class ProgressOnlyUi : IAgentUserInterface
    {
        public void Say(string message)
        {
            // The connector never says anything user-facing yet.
        }

        public void ShowProgress(AgentProgressEvent progressEvent)
            => Console.WriteLine($"[{progressEvent.Type}] {progressEvent.Detail}");

        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(true);

        public Task<string> AskAsync(string question) => Task.FromResult(string.Empty);

        public void ShowDiff(FileChange change)
        {
        }

        public IDisposable BeginWait(string message) => new NopScope();

        public Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(ChoiceResult.CancelledMenu());

        private sealed class NopScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
