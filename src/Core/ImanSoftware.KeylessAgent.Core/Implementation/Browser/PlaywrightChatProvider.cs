using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.Extensions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
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

    private readonly BrowserConnector _connector;
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
            // The launch command line travels into the turn log as a "chrome-launched" event,
            // so every turn can prove which anti-occlusion flags Chrome received.
            _connector.LogEventSink = options.LogEvent;

            var connected = await _connector.ConnectAsync(options.Browser, new ProgressOnlyUi(), cancellationToken, options.Timings, options.ResumeChatUrl);
            if (connected.IsFailure)
                return Outcome.Failure<IChatSession>(connected.Error);

            // Remember the REAL options this session connected with (port + profile dir) so
            // the App.OnExit Chrome-close safety net matches ownership against what was
            // actually used, even if keyless.json changed afterwards.
            ChromeLaunchTracker.Remember(options.Browser, BrowserConnector.ResolveUserDataDir(options.Browser));

            var (attachment, rawPage) = connected.Data;
            var page = (IPage)rawPage;
            var log = new LoggerAdapter<PlaywrightChatProvider>(_loggerFactory);

            // Hide the window IMMEDIATELY after the tab is open: Target.createTarget now opens
            // the tab in the background, but Chrome can still move the window while the tab
            // loads. The post-login hide below stays. Both Minimized and Offscreen mean
            // offscreen; Normal is never forced.
            if (options.Browser.MinimizeAfterLogin &&
                !string.Equals(options.Browser.WindowMode?.Trim(), "Normal", StringComparison.OrdinalIgnoreCase))
            {
                await HideWindowAsync(page, options.Browser, attachment.LaunchedProcessId, log, cancellationToken);
            }

            // Resume safety net: the connector already selected/navigated the tab by
            // CONVERSATION ID; navigate here only when the picked tab is still not on the
            // saved conversation (e.g. the connector's navigation failed). Commit returns as
            // soon as the navigation is acknowledged; a hung navigation cannot cost a minute.
            // A dead conversation redirects to the base URL - the loop's chat-resume-failed
            // handling takes over from there.
            if (ChatUrl.IsConversationUrl(options.ResumeChatUrl) &&
                !ChatUrl.SameConversation(page.Url, options.ResumeChatUrl))
            {
                try
                {
                    await page.GotoAsync(options.ResumeChatUrl!, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Commit,
                        Timeout = 15_000,
                    });
                }
                catch (Exception ex)
                {
                    new LoggerAdapter<PlaywrightChatProvider>(NullLoggerFactory.Instance).LogWarning(
                        "Resume navigation failed best-effort: {Message}", ex.Message);
                }
            }

            var selectors = await SelectorsCatalog.LoadAsync(options.WorkspaceRoot, options.Browser.SelectorsFile, cancellationToken, loggerFactory: _loggerFactory);
            if (selectors.IsFailure)
                return Outcome.Failure<IChatSession>(selectors.Error);

            IChatPage chatPage = new PlaywrightChatPage(
                page, selectors.Data, options.Browser.Debug,
                new LoggerAdapter<PlaywrightChatPage>(_loggerFactory));

            await WaitForLoginIfNeeded(chatPage, selectors.Data, options, cancellationToken);

            // One composer reset right after the session starts: stale text or attachment
            // chips from an earlier failed turn must not leak into the first message.
            try
            {
                var reset = await chatPage.ResetComposerAsync(options.Browser.ReplyTimeoutSeconds, cancellationToken);
                if (!reset.Clean)
                    new LoggerAdapter<PlaywrightChatProvider>(_loggerFactory).LogWarning(
                        "The composer is not clean after StartSession (method: {Method}); the next send resets it again.", reset.Method);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                new LoggerAdapter<PlaywrightChatProvider>(_loggerFactory).LogWarning(
                    "The post-StartSession composer reset failed best-effort: {Message}", ex.Message);
            }

            // Logged in (or just logged in): hide the window again — the login probe or the
            // user's own interaction may have moved it back into view.
            if (options.Browser.MinimizeAfterLogin)
                await HideWindowAsync(page, options.Browser, attachment.LaunchedProcessId, log, cancellationToken);

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
    /// Test seam: replaces the login window controller (the login flow shows the hidden
    /// window once so the user can log in — pure CDP window move).
    /// </summary>
    internal Func<BrowserOptions, IChromeWindowController> WindowControllerFactory { get; set; } =
        options => new ChromeWindowController();

    /// <summary>
    /// Positive-first login detection: ONE parallel wait races the chat InputBox against the
    /// LoginIndicator. If the input appears first the user is logged in — no login wait ever
    /// runs, and absent elements never consume a per-fallback timeout. Only a winning login
    /// indicator SHOWS the window and waits for the manual login (LoginWaitSeconds).
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

        // The login page would be INVISIBLE in a hidden window (minimized/offscreen): show it
        // once, explicitly (pure CDP window move — no BringToFront focus steal of the terminal).
        await WindowControllerFactory(options.Browser).ShowAsync(options.Browser, 0, cancellationToken);

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
    /// Hides the chat window OFFSCREEN so a successful launch does not steal the user's
    /// focus: CDP Browser.setWindowBounds {left:-32000, top:-32000, width:1280, height:900,
    /// windowState:normal} with a read-back verification and a normal→offscreen recovery,
    /// then a Win32 fallback that finds the real Chrome top-level windows of our chrome.exe
    /// processes and ShowWindow(SW_HIDE)s them (the launched launcher PID often has
    /// MainWindowHandle == 0). Previously this used windowState:minimized, which throttles
    /// Chrome's renderer and truncates DeepSeek streaming replies; the window is now MOVED
    /// offscreen with normal state — the user cannot see it, Chrome does not throttle it.
    /// Recover with 'keyless show' or the WPF Show Chrome button. Best-effort: every
    /// failure is swallowed.
    /// </summary>
    internal static async Task HideWindowAsync(
        IPage page, BrowserOptions browserOptions, int launchedProcessId,
        ILoggerAdapter<PlaywrightChatProvider>? log = null, CancellationToken cancellationToken = default)
    {
        await ChromeWindowMinimizer.HideAsync(
            page, launchedProcessId, BrowserConnector.ResolveUserDataDir(browserOptions),
            browserOptions.ChromeDebugPort, log, cancellationToken);
    }

    /// <summary>
    /// Dispose policy: the chat tab STAYS OPEN by default — the conversation continues across
    /// turns and across runs (the next run reuses the tab and the saved chat URL). Only
    /// Browser.CloseChromeOnExit closes the window/context when the session ends.
    /// DISTINCTION from the WPF App.OnExit override: CloseChromeOnExit governs THIS per-turn
    /// session disposal while the app runs (tab/context left open between turns); App.OnExit
    /// is a separate, always-on safety net that closes the actual OS-level Chrome process the
    /// app itself launched when the whole application exits. Keep them separate.
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
            => Console.WriteLine($"› {StepLabel(progressEvent.Type)}: {progressEvent.Detail}");

        private static string StepLabel(AgentEventType type) => type switch
        {
            AgentEventType.SessionStarting => "setup",
            AgentEventType.ConnectingToBrowser => "connect",
            AgentEventType.OpeningChat => "chat",
            AgentEventType.UploadingAttachments => "attachments",
            AgentEventType.SendingMessage => "send",
            AgentEventType.WaitingForReply => "reply",
            AgentEventType.ValidatingReply => "reply",
            AgentEventType.ExecutingCommand => "execute",
            AgentEventType.SendingResults => "results",
            AgentEventType.RefreshingSnapshot => "snapshot",
            AgentEventType.TurnFinished => "done",
            _ => type.ToString().ToLowerInvariant(),
        };

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
