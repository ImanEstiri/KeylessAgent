using System.Diagnostics;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.Logging;
using ImanSoftware.Outcomes;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Establishes the browser connection with explicit, reported decisions:
/// 1. probe the debug port → attach; 2. optionally auto-launch the REAL Chrome with a dedicated
/// profile (mandatory since Chrome 136 — the default user-data-dir ignores the debug port);
/// 3. only with an explicit opt-in, a Playwright-managed fallback browser. Never navigates the
/// user's existing tabs; reuses an existing chat.deepseek.com tab or opens a new TAB; focuses it.
/// </summary>
internal sealed class BrowserConnector : IBrowserConnector
{
    public const string ChatBaseUrl = "https://chat.deepseek.com/";

    private readonly IBrowserEnvironment _environment;
    private readonly ILoggerAdapter<BrowserConnector> _log;

    /// <summary>
    /// Test seam: when set, replaces the Playwright tab-open/focus path.
    /// </summary>
    internal Func<BrowserAttachment, CancellationToken, Task<Outcome<object>>>? OpenTabOverride { get; set; }

    /// <summary>
    /// When set (by the provider, from ChatSessionOptions.LogEvent), the launch command line
    /// is forwarded here as a "chrome-launched" event with the full arguments payload.
    /// </summary>
    internal Func<string, object, Task>? LogEventSink { get; set; }

    public BrowserConnector()
        : this(new BrowserEnvironment(), null)
    {
    }

    internal BrowserConnector(IBrowserEnvironment environment, ILoggerAdapter<BrowserConnector>? log)
    {
        _environment = environment;
        _log = log ?? new LoggerAdapter<BrowserConnector>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
    }

    /// <inheritdoc />
    public async Task<Outcome<(BrowserAttachment Attachment, object Page)>> ConnectAsync(
        BrowserOptions options,
        IAgentUserInterface ui,
        CancellationToken cancellationToken = default,
        ITimingRecorder? timings = null,
        string? resumeChatUrl = null)
    {
        try
        {
            var userDataDir = ResolveUserDataDir(options);
            var playwright = await Playwright.CreateAsync();

            // --- Step 1: probe the debug port and attach when it is already open ---
            ui.ShowProgress(new AgentProgressEvent(
                AgentEventType.ConnectingToBrowser,
                $"Probing port {options.ChromeDebugPort}…"));
            if (await _environment.IsDebugPortOpenAsync(options.ChromeDebugPort, cancellationToken))
            {
                var attach = await AttachOverCdpAsync(playwright, options, ui, cancellationToken);
                if (attach.IsSuccess)
                {
                    ui.ShowProgress(new AgentProgressEvent(
                        AgentEventType.ConnectingToBrowser,
                        $"Attached to Chrome on port {options.ChromeDebugPort}."));
                    return await OpenChatTabAsync(attach.Data, userDataDir, options, resumeChatUrl, ui, timings, cancellationToken);
                }

                // The port answers but CDP cannot attach: report the reason, do not fall through.
                _log.LogWarning("Port {Port} is open but CDP attach failed: {Reason}",
                    options.ChromeDebugPort, attach.Error.Message);
                ui.ShowProgress(new AgentProgressEvent(
                    AgentEventType.ConnectingToBrowser,
                    $"Port {options.ChromeDebugPort} is open but attach failed: {attach.Error.Message}"));
                return Outcome.Failure<(BrowserAttachment, object)>(new OutcomeError(
                    $"Port {options.ChromeDebugPort} is open but CDP attach failed: {attach.Error.Message}. " +
                    $"Close the Chrome that owns port {options.ChromeDebugPort} or change Browser.ChromeDebugPort.",
                    ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Problem));
            }

            // --- Step 2: auto-launch the real Chrome with the dedicated profile ---
            if (options.AutoLaunchChrome)
            {
                var launch = await LaunchRealChromeAsync(playwright, options, userDataDir, ui, cancellationToken);
                if (launch.IsSuccess)
                {
                    return await OpenChatTabAsync(launch.Data, userDataDir, options, resumeChatUrl, ui, timings, cancellationToken);
                }

                // Chrome not found / not started: actionable failure with the exact command line.
                var commandLine = $"chrome --remote-debugging-port={options.ChromeDebugPort} " +
                                  "--remote-debugging-address=127.0.0.1 " +
                                  $"--user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check " +
                                  "https://chat.deepseek.com/";
                return Outcome.Failure<(BrowserAttachment, object)>(new OutcomeError(
                    $"Chrome could not be located or started ({launch.Error.Message}). " +
                    $"Start it manually once with:{Environment.NewLine}    {commandLine}" +
                    $"{Environment.NewLine}or set Browser.ChromePath in keyless.json.",
                    ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Problem));
            }

            // --- Step 3: explicit opt-in Playwright fallback ---
            if (options.AllowPlaywrightLaunch)
            {
                ui.ShowProgress(new AgentProgressEvent(
                    AgentEventType.ConnectingToBrowser,
                    "Starting a separate Playwright-managed browser (its own profile — not your Chrome)."));
                var fallback = await playwright.Chromium.LaunchPersistentContextAsync(
                    userDataDir,
                    new BrowserTypeLaunchPersistentContextOptions { Headless = false });
                var attachment = new BrowserAttachment
                {
                    Context = fallback,
                    Kind = BrowserConnectionKind.PlaywrightLaunched,
                    LaunchedProcess = true,
                    UserDataDir = userDataDir,
                };
                var fallbackPage = fallback.Pages.Count > 0 ? fallback.Pages[0] : await fallback.NewPageAsync();
                fallbackPage.SetDefaultTimeout(5_000);
                return Outcome.Successful((attachment, (object)fallbackPage));
            }

            return Outcome.Failure<(BrowserAttachment, object)>(new OutcomeError(
                "The debug port is not open and Browser.AutoLaunchChrome is disabled. " +
                "Start Chrome manually with the command line shown in the documentation, or enable AutoLaunchChrome.",
                ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Problem));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Outcome.Failure<(BrowserAttachment, object)>(new OutcomeError(
                $"Browser connection failed: {ex.GetType().Name}: {ex.Message}",
                ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Failure));
        }
    }

    internal static string ResolveUserDataDir(BrowserOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.UserDataDir))
            return Path.GetFullPath(options.UserDataDir);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KeylessAgent", "chrome-profile");
    }

    private async Task<Outcome<BrowserAttachment>> AttachOverCdpAsync(
        IPlaywright playwright, BrowserOptions options, IAgentUserInterface ui, CancellationToken cancellationToken)
    {
        try
        {
            var (browser, context) = await _environment.AttachCdpAsync(playwright, options.ChromeDebugPort, cancellationToken);

            return Outcome.Successful(new BrowserAttachment
            {
                Browser = browser,
                Context = context,
                Kind = BrowserConnectionKind.AttachedToRunningChrome,
                LaunchedProcess = false,
                UserDataDir = ResolveUserDataDir(options),
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Outcome.Failure<BrowserAttachment>(new OutcomeError(
                $"CDP attach failed: {ex.Message}",
                ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Failure));
        }
    }

    private async Task<Outcome<BrowserAttachment>> LaunchRealChromeAsync(
        IPlaywright playwright, BrowserOptions options, string userDataDir, IAgentUserInterface ui, CancellationToken cancellationToken)
    {
        var chromePath = await _environment.LocateChromeAsync(options.ChromePath, cancellationToken);
        if (chromePath is null)
        {
            return Outcome.Failure<BrowserAttachment>(new OutcomeError(
                "chrome.exe was not found (set Browser.ChromePath).",
                ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.NotFound));
        }

        ui.ShowProgress(new AgentProgressEvent(
            AgentEventType.ConnectingToBrowser,
            $"Starting Chrome with the KeylessAgent profile ({userDataDir})…"));

        // Screen diagnostics logged WITH the launch: raw monitor size vs work area (the
        // difference is the taskbar). If the two ever stop differing by the taskbar's real
        // height - DPI virtualization, auto-hide taskbar - it is visible in the turn log
        // without reproducing anything manually.
        var rawScreen = BrowserEnvironment.RawScreenSize();
        var workArea = BrowserEnvironment.PrimaryScreenSize();
        _log.LogInformation(
            "Screen diagnostics: raw screen {RawWidth}x{RawHeight}, work area {WorkWidth}x{WorkHeight} (difference = taskbar).",
            rawScreen.Width, rawScreen.Height, workArea.Width, workArea.Height);

        var processId = await _environment.StartChromeAsync(chromePath, options.ChromeDebugPort, userDataDir, options.WindowMode, cancellationToken,
            onCommandLine: arguments =>
            {
                _log.LogInformation("Chrome launched with command line: {Arguments}", arguments);
                _ = LogEventSink?.Invoke("chrome-launched", new
                {
                    Arguments = arguments,
                    RawScreenSize = $"{rawScreen.Width}x{rawScreen.Height}",
                    WorkAreaSize = $"{workArea.Width}x{workArea.Height}",
                });
            });

        // Wait until /json/version answers (30s), then attach.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _environment.IsDebugPortOpenAsync(options.ChromeDebugPort, cancellationToken))
            {
                var attach = await AttachOverCdpAsync(playwright, options, ui, cancellationToken);
                if (attach.IsSuccess)
                {
                    VerifyLaunchedWindowBounds(processId, userDataDir, options, workArea);

                    ui.ShowProgress(new AgentProgressEvent(
                        AgentEventType.ConnectingToBrowser,
                        "Started Chrome with the KeylessAgent profile. Log in to DeepSeek in that window once."));
                    return Outcome.Successful(attach.Data with
                    {
                        Kind = BrowserConnectionKind.AutoLaunchedChrome,
                        LaunchedProcess = true,
                        LaunchedProcessId = processId,
                    });
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        return Outcome.Failure<BrowserAttachment>(new OutcomeError(
            $"Chrome was started (pid {processId}) but the debug port did not open within 30s.",
            ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Failure));
    }

    /// <summary>
    /// Picks an existing chat.deepseek.com tab (never navigating the user's other tabs) or
    /// opens a new TAB in the existing window, then brings it to front.
    /// </summary>
    private async Task<Outcome<(BrowserAttachment Attachment, object Page)>> OpenChatTabAsync(
        BrowserAttachment attachment,
        string userDataDir,
        BrowserOptions options,
        string? resumeChatUrl,
        IAgentUserInterface ui,
        ITimingRecorder? timings,
        CancellationToken cancellationToken)
    {
        timings?.Start("tab-open");
        try
        {
        if (OpenTabOverride is not null)
        {
            var pageOutcome = await OpenTabOverride(attachment, cancellationToken);
            if (pageOutcome.IsFailure)
                return Outcome.Failure<(BrowserAttachment, object)>(pageOutcome.Error);
            return Outcome.Successful((attachment, pageOutcome.Data));
        }

        var context = (IBrowserContext)attachment.Context;
        IPage page;

        // Tab selection by CONVERSATION ID (never a base-URL prefix, which matches every
        // DeepSeek tab and made workspaces share one conversation): reuse the tab of the
        // saved conversation, navigate a base/new-chat tab to it, or open a new tab - but
        // never touch a tab showing a DIFFERENT conversation.
        var (selected, action) = ChatUrl.SelectChatTab(context.Pages, resumeChatUrl);
        switch (action)
        {
            case ChatUrl.TabSelectionAction.ReuseSameConversation:
                page = selected!;
                ui.ShowProgress(new AgentProgressEvent(
                    AgentEventType.OpeningChat, "Reusing the tab of the saved conversation."));
                break;

            case ChatUrl.TabSelectionAction.NavigateExistingBaseTab:
                page = selected!;
                ui.ShowProgress(new AgentProgressEvent(
                    AgentEventType.OpeningChat, "Navigating an empty DeepSeek tab to the saved conversation."));
                await page.GotoAsync(resumeChatUrl!, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.Commit,
                    Timeout = 15_000,
                });
                break;

            default:
            {
                // No saved conversation: a NEW tab at the saved URL (or the base URL when
                // there is none). An existing tab on a DIFFERENT conversation is never touched.
                var targetUrl = ChatUrl.IsConversationUrl(resumeChatUrl) ? resumeChatUrl! : ChatBaseUrl;

                // A browser-level CDP session opens a TAB in the existing window when one exists.
                var anchorPage = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
                var openSession = await context.NewCDPSessionAsync(anchorPage);
                var created = await openSession.SendAsync("Target.createTarget", ComposeCreateTargetParams(targetUrl));
                var createdTargetId = ((System.Text.Json.JsonElement)created)
                    .GetProperty("targetId").GetString() ?? string.Empty;

                // One-time wake-up pulse: a tab created with background:true that is never
                // activated sits in Chrome's most aggressive throttling bucket (background +
                // occluded + never-user-activated) for its whole life, which can kill
                // timing-sensitive operations like attachment uploads while chat-completion
                // requests survive. ONE activateTarget right here — in the same tick as the
                // create and BEFORE the hide step — un-sticks that class for the renderer's
                // lifetime without a visible focus steal. Never done for reused tabs (those
                // were activated at some point already).
                await openSession.SendAsync("Target.activateTarget", new Dictionary<string, object>
                {
                    ["targetId"] = createdTargetId,
                });
                await openSession.DetachAsync();

                ui.ShowProgress(new AgentProgressEvent(
                    AgentEventType.OpeningChat, "Opened and activated a new DeepSeek tab (one-time throttling wake-up)."));
                _ = LogEventSink?.Invoke("tab-activated-once", new
                {
                    TargetId = createdTargetId,
                    Url = targetUrl,
                });

                var resumeId = ChatUrl.ConversationId(targetUrl);
                page = resumeId is null
                    ? await WaitForPageAsync(context, ChatBaseUrl, cancellationToken)
                    : await WaitForConversationPageAsync(context, resumeId, cancellationToken);
                break;
            }
        }

        // Safety net: no Playwright call may ride the 30 s library default. Every hot call
        // carries its own explicit timeout; this caps anything missed, on every connection
        // path (attach, auto-launch, doctor).
        page.SetDefaultTimeout(5_000);

        // Normal operation: no focus, no restore — the window stays minimized.

        return Outcome.Successful((attachment, (object)page));
        }
        finally
        {
            timings?.Stop("tab-open");
        }
    }

    /// <summary>
    /// Follow-up verification right after the launch attach: locate the launched Chrome's
    /// real HWND and GetWindowRect it, logging the ACTUAL bounds next to the REQUESTED
    /// --window-position/--window-size. Chrome can silently clamp or reposition a window
    /// (e.g. enforce a minimum size) - a root cause distinct from work-area/DPI issues that
    /// previously had zero visibility. Read-only: never moves or closes anything.
    /// </summary>
    private void VerifyLaunchedWindowBounds(
        int launchedProcessId, string userDataDir, BrowserOptions options, (int Width, int Height) workArea)
    {
        if (OpenTabOverride is not null)
            return; // test seam: no real Chrome exists to verify.

        try
        {
            var requested = BrowserEnvironment.RequestedWindowBounds(options.WindowMode, workArea);
            if (requested is null)
                return; // Normal never forces the window - nothing to verify.

            // The debug port can open before the window exists: poll briefly for the HWND.
            IReadOnlyList<IntPtr> handles = [];
            for (var attempt = 0; attempt < 6 && handles.Count == 0; attempt++)
            {
                if (attempt > 0)
                    Thread.Sleep(500);
                handles = ChromeWindowMover.FindOurChromeWindows(launchedProcessId, userDataDir, options.ChromeDebugPort);
            }

            if (handles.Count == 0)
            {
                _log.LogWarning(
                    "Launched-window verification: no Chrome window of our chrome.exe processes was found (pid tree of {ProcessId}) within 3s of the launch.",
                    launchedProcessId);
                return;
            }

            // Multiple windows can exist (restored session): verify the LARGEST one - that is
            // the main window the geometry request targeted.
            var actual = handles
                .Select(ChromeWindowMover.GetWindowBoundsWin32)
                .OfType<(int Left, int Top, int Width, int Height)>()
                .OrderByDescending(b => (long)b.Width * b.Height)
                .FirstOrDefault();

            var matches = Math.Abs(actual.Left - requested.Value.Left) <= 8 &&
                Math.Abs(actual.Top - requested.Value.Top) <= 8 &&
                Math.Abs(actual.Width - requested.Value.Width) <= 8 &&
                Math.Abs(actual.Height - requested.Value.Height) <= 8;

            var level = matches ? LogLevel.Information : LogLevel.Warning;
            _log.Log(level,
                "Launched-window verification ({Result}): requested {Requested}, actual {Actual} (mode {Mode}, tolerance 8px).",
                matches ? "matches" : "MISMATCH",
                $"{requested.Value.Left},{requested.Value.Top} {requested.Value.Width}x{requested.Value.Height}",
                $"{actual.Left},{actual.Top} {actual.Width}x{actual.Height}",
                options.WindowMode);

            _ = LogEventSink?.Invoke("chrome-window-bounds", new
            {
                Requested = $"{requested.Value.Left},{requested.Value.Top} {requested.Value.Width}x{requested.Value.Height}",
                Actual = $"{actual.Left},{actual.Top} {actual.Width}x{actual.Height}",
                Matches = matches,
                Mode = options.WindowMode,
            });
        }
        catch (Exception ex)
        {
            // Verification is diagnostics, never a connection failure.
            _log.LogWarning("Launched-window verification failed best-effort: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// The Target.createTarget parameters for the chat tab. <c>background: true</c> matters:
    /// without it Chrome ACTIVATES the new tab, which restores a minimized window — the exact
    /// "window pops up again" report. <c>newWindow: false</c> keeps it a tab in that window.
    /// </summary>
    internal static Dictionary<string, object> ComposeCreateTargetParams(string url) => new()
    {
        ["url"] = url,
        ["newWindow"] = false,
        ["background"] = true,
    };

    /// <summary>Waits for a page whose URL is the SAME conversation (id equality, not a prefix).</summary>
    private static async Task<IPage> WaitForConversationPageAsync(
        IBrowserContext context, string conversationId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = context.Pages.FirstOrDefault(p =>
                ChatUrl.ConversationId(p.Url) == conversationId);
            if (match is not null)
                return match;

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("The resumed DeepSeek conversation tab did not appear within 15s.");
    }

    private static async Task<IPage> WaitForPageAsync(IBrowserContext context, string urlPrefix, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = context.Pages.FirstOrDefault(p =>
                p.Url.StartsWith(urlPrefix, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("The new DeepSeek tab did not appear within 15s.");
    }

    private static async Task<string> GetTargetIdAsync(ICDPSession session)
    {
        var response = await session.SendAsync("Target.getTargetInfo");
        var element = (System.Text.Json.JsonElement)response;
        return element.GetProperty("targetInfo").GetProperty("targetId").GetString() ?? string.Empty;
    }
}
