using System.Diagnostics;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.Logging;
using ImanSoftware.Outcomes;
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
        CancellationToken cancellationToken = default)
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
                    return await OpenChatTabAsync(attach.Data, userDataDir, options, ui, cancellationToken);
                }

                // The port answers but CDP cannot attach: report the reason, do not fall through.
                _log.LogWarning("Port {Port} is open but CDP attach failed: {Reason}",
                    options.ChromeDebugPort, attach.Error.Message);
                ui.ShowProgress(new AgentProgressEvent(
                    AgentEventType.ConnectingToBrowser,
                    $"Port {options.ChromeDebugPort} is open but attach failed: {attach.Error.Message}"));
            }

            // --- Step 2: auto-launch the real Chrome with the dedicated profile ---
            if (options.AutoLaunchChrome)
            {
                var launch = await LaunchRealChromeAsync(playwright, options, userDataDir, ui, cancellationToken);
                if (launch.IsSuccess)
                {
                    return await OpenChatTabAsync(launch.Data, userDataDir, options, ui, cancellationToken);
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
        var processId = await _environment.StartChromeAsync(chromePath, options.ChromeDebugPort, userDataDir, cancellationToken);

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
        IAgentUserInterface ui,
        CancellationToken cancellationToken)
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

        var existing = options.ReuseExistingTab
            ? context.Pages.FirstOrDefault(p =>
                p.Url.StartsWith(ChatBaseUrl, StringComparison.OrdinalIgnoreCase))
            : null;

        if (existing is not null)
        {
            page = existing;
            ui.ShowProgress(new AgentProgressEvent(
                AgentEventType.OpeningChat, "Reusing the existing DeepSeek tab."));
        }
        else
        {
            // A browser-level CDP session opens a TAB in the existing window when one exists.
            var anchorPage = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
            var openSession = await context.NewCDPSessionAsync(anchorPage);
            await openSession.SendAsync("Target.createTarget",
                new Dictionary<string, object> { ["url"] = ChatBaseUrl, ["newWindow"] = false });
            await openSession.DetachAsync();

            page = await WaitForPageAsync(context, ChatBaseUrl, cancellationToken);

            ui.ShowProgress(new AgentProgressEvent(
                AgentEventType.OpeningChat, "Opened a new DeepSeek tab."));
        }

        // Focus: bring the tab to the front (CDP activateTarget as well).
        try
        {
            await page.BringToFrontAsync();
            var focusSession = await context.NewCDPSessionAsync(page);
            await focusSession.SendAsync("Target.activateTarget",
                new Dictionary<string, object> { ["targetId"] = await GetTargetIdAsync(focusSession) });
            await focusSession.DetachAsync();
        }
        catch
        {
            // Focus is best-effort.
        }

        return Outcome.Successful((attachment, (object)page));
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
