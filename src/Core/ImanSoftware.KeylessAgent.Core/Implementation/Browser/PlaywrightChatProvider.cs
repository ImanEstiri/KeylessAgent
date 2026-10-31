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
            var connected = await _connector.ConnectAsync(options.Browser, new ProgressOnlyUi(), cancellationToken, options.Timings);
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

            IChatPage chatPage = new PlaywrightChatPage(
                page, selectors.Data, options.Browser.Debug,
                new LoggerAdapter<PlaywrightChatPage>(_loggerFactory));

            await WaitForLoginIfNeeded(chatPage, selectors.Data, options, cancellationToken);

            // Logged in (or just logged in): get the window out of the user's way.
            if (options.Browser.MinimizeAfterLogin)
                await MinimizeWindowAsync(page, cancellationToken);

            IChatSession session = new PlaywrightChatSession(
                chatPage,
                () => DisposeAsync(page, options.Browser, attachment.Kind),
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
    /// Positive-first login detection: ONE parallel wait races the chat InputBox against the
    /// LoginIndicator. If the input appears first the user is logged in — no login wait ever
    /// runs, and absent elements never consume a per-fallback timeout. Only a winning login
    /// indicator focuses the tab and waits for the manual login (LoginWaitSeconds).
    /// </summary>
    internal async Task WaitForLoginIfNeeded(
        IChatPage page, SelectorsCatalog selectors, ChatSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        var probeSelectors = selectors.InputBox.Concat(selectors.LoginIndicator).ToArray();
        options.Timings?.Start("login-probe");
        int? winner;
        try
        {
            winner = await page.WaitForAnyVisibleAsync(
                probeSelectors, options.Browser.LoginProbeSeconds * 1_000, cancellationToken);
        }
        finally
        {
            options.Timings?.Stop("login-probe");
        }

        if (winner is null || winner < selectors.InputBox.Length)
            return; // the chat input is there (or nothing matched — the send path will tell).

        var logger = new LoggerAdapter<PlaywrightChatProvider>(NullLoggerFactory.Instance);
        logger.LogWarning("Login page detected; waiting up to {Seconds}s for a manual login…", LoginWaitSeconds);
        await page.FocusTabAsync(cancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(LoginWaitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(2_000, cancellationToken);
            options.Timings?.Start("login-probe");
            bool stillOnLogin;
            try
            {
                stillOnLogin = await page.IsAnyVisibleAsync(selectors.LoginIndicator, cancellationToken);
            }
            finally
            {
                options.Timings?.Stop("login-probe");
            }

            if (!stillOnLogin)
                return; // login done; the caller minimizes.
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
    internal static async ValueTask DisposeAsync(IPage page, BrowserOptions browserOptions, BrowserConnectionKind kind)
    {
        // Never close a Chrome we only ATTACHED to — it is the user's own browser. A Chrome
        // we launched ourselves is ours to close when CloseChromeOnExit is set; the old
        // pages.Count == 0 guard could almost never be true (the agent's own chat tab is
        // always a page), so the context was silently never closed.
        if (!browserOptions.CloseChromeOnExit || kind is BrowserConnectionKind.AttachedToRunningChrome)
            return;

        try
        {
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
