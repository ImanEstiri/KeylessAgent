using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.Logging;
using ImanSoftware.Outcomes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Brings the (offscreen or minimized) Chrome window into view on demand — or puts it back —
/// through pure CDP window-bounds calls. Used by <c>keyless show</c>/<c>keyless hide</c>, the
/// doctor page's Show button, the <c>keyless chrome</c> command and the login flow. No
/// automation, no page navigation: a window move only, so a running turn is never disturbed.
/// </summary>
public interface IChromeWindowController
{
    /// <summary>
    /// Shows the chat window at (100,100) 1280x900 and focuses it. <paramref name="peekSeconds"/>
    /// of 0 keeps it visible until <see cref="HideAsync"/> or the user closes it; a positive
    /// value returns the window offscreen after that many seconds.
    /// </summary>
    Task<Outcome> ShowAsync(BrowserOptions options, int peekSeconds, CancellationToken ct = default);

    /// <summary>Moves the chat window offscreen (-32000,-32000), still "normal" so Chrome keeps rendering.</summary>
    Task<Outcome> HideAsync(BrowserOptions options, CancellationToken ct = default);
}

/// <summary>CDP-backed implementation: connect over the debug port, resolve the window, move it.</summary>
internal sealed class ChromeWindowController : IChromeWindowController
{
    private readonly IBrowserEnvironment _environment;
    private readonly ILoggerAdapter<ChromeWindowController> _log;

    /// <summary>Test seam: replaces Playwright.CreateAsync (which spawns the driver).</summary>
    internal Func<CancellationToken, Task<IPlaywright>> PlaywrightFactory { get; set; } =
        ct => Playwright.CreateAsync();

    public ChromeWindowController(IBrowserEnvironment? environment = null, ILoggerFactory? loggerFactory = null)
    {
        _environment = environment ?? new BrowserEnvironment();
        _log = new LoggerAdapter<ChromeWindowController>(loggerFactory ?? NullLoggerFactory.Instance);
    }

    /// <inheritdoc />
    public async Task<Outcome> ShowAsync(BrowserOptions options, int peekSeconds, CancellationToken ct = default)
    {
        // Same geometry as HideAsync: the peek's return-to-hidden lands in the tiny corner.
        var hideBounds = BrowserEnvironment.TinyCornerBounds();
        var showBounds = (Left: 100, Top: 100, Width: 1280, Height: 900);
        var info = (Action<string>)(message => _log.LogInformation("{Message}", message));
        var debug = (Action<string>)(message => _log.LogDebug("{Message}", message));

        var cdpVerified = false;
        try
        {
            if (!await _environment.IsDebugPortOpenAsync(options.ChromeDebugPort, ct))
                return PortClosed(options);

            var playwright = await PlaywrightFactory(ct);
            var browser = await ConnectAsync(playwright, options);
            var context = browser.Contexts.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "The Chrome on the debug port exposes no browser context (is it still starting?).");
            // With several tabs open the FIRST page is often not the chat: prefer the page
            // whose URL is the chat host so the CHAT window is the one that gets moved.
            var page = ChromeWindowMover.PickChatPage(context) ?? await context.NewPageAsync();
            debug($"Chat window picked from page: {page.Url}");

            var session = await page.Context.NewCDPSessionAsync(page);
            try
            {
                var windowId = await ResolveWindowIdAsync(session, ct);
                info($"CDP Show: Browser.setWindowBounds(windowId {windowId}, bounds {{left: {showBounds.Left}, top: {showBounds.Top}, width: {showBounds.Width}, height: {showBounds.Height}, windowState: normal}})");
                cdpVerified = await ChromeWindowMover.TryMoveViaCdpAsync(session, windowId, showBounds, "Show", info, debug);
                if (!cdpVerified)
                    return await RecoverViaWin32OrFailAsync(options, showBounds, "Show", debug);

                var targetId = await ResolveTargetIdAsync(session, ct);
                info($"CDP Show: Target.activateTarget(targetId {targetId})");
                await session.SendAsync("Target.activateTarget", new Dictionary<string, object>
                {
                    ["targetId"] = targetId,
                });

                if (peekSeconds > 0)
                {
                    await Task.Delay(peekSeconds * 1000, ct);
                    info($"CDP Hide: Browser.setWindowBounds(windowId {windowId}, bounds {{left: {hideBounds.Left}, top: {hideBounds.Top}, width: {hideBounds.Width}, height: {hideBounds.Height}, windowState: normal}})");
                    // The peek's return is best-effort: the SHOW already succeeded.
                    await ChromeWindowMover.TryMoveViaCdpAsync(session, windowId, hideBounds, "Hide", info, debug);
                }

                return Outcome.Successful();
            }
            finally
            {
                await session.DetachAsync();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            debug($"CDP show failed: {ex.Message}; falling back to Win32.");
        }

        if (!cdpVerified)
            return await RecoverViaWin32OrFailAsync(options, showBounds, "Show", debug);
        return Outcome.Successful();
    }

    /// <inheritdoc />
    public async Task<Outcome> HideAsync(BrowserOptions options, CancellationToken ct = default)
    {
        // The hide geometry: a tiny 80x80 square in the screen's WORK AREA bottom-right
        // corner - NOT fully offscreen, which Chrome treats as occluded and throttles
        // (turn-17/18). Not hidden behind the taskbar either (SPI_GETWORKAREA).
        var hideBounds = BrowserEnvironment.TinyCornerBounds();
        var info = (Action<string>)(message => _log.LogInformation("{Message}", message));
        var debug = (Action<string>)(message => _log.LogDebug("{Message}", message));

        var cdpVerified = false;
        try
        {
            if (!await _environment.IsDebugPortOpenAsync(options.ChromeDebugPort, ct))
                return PortClosed(options);

            var playwright = await PlaywrightFactory(ct);
            var browser = await ConnectAsync(playwright, options);
            var context = browser.Contexts.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "The Chrome on the debug port exposes no browser context (is it still starting?).");
            var page = ChromeWindowMover.PickChatPage(context) ?? await context.NewPageAsync();
            debug($"Chat window picked from page: {page.Url}");

            var session = await page.Context.NewCDPSessionAsync(page);
            try
            {
                var windowId = await ResolveWindowIdAsync(session, ct);
                info($"CDP Hide: Browser.setWindowBounds(windowId {windowId}, bounds {{left: {hideBounds.Left}, top: {hideBounds.Top}, width: {hideBounds.Width}, height: {hideBounds.Height}, windowState: normal}})");
                cdpVerified = await ChromeWindowMover.TryMoveViaCdpAsync(session, windowId, hideBounds, "Hide", info, debug);
            }
            finally
            {
                await session.DetachAsync();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            debug($"CDP hide failed: {ex.Message}; falling back to Win32.");
        }

        if (!cdpVerified)
            return await RecoverViaWin32OrFailAsync(options, hideBounds, "Hide", debug);
        return Outcome.Successful();
    }

    /// <summary>
    /// The shared tail of both directions: when CDP could not verify the move, try the Win32
    /// fallback (show: SW_SHOWNOACTIVATE + visible/not-minimized read-back; hide: SW_HIDE +
    /// invisible read-back) - our processes are matched by profile directory and debug port.
    /// When even the fallback cannot verify anything, FAIL honestly instead of reporting a
    /// move that never happened.
    /// </summary>
    private async Task<Outcome> RecoverViaWin32OrFailAsync(
        BrowserOptions options, (int Left, int Top, int Width, int Height) bounds, string label, Action<string> debug)
    {
        debug($"CDP {label} was not verified; falling back to Win32.");
        var win32Verified = ChromeWindowMover.ShowWindowsViaWin32(
            launchedProcessId: 0,
            BrowserConnector.ResolveUserDataDir(options),
            options.ChromeDebugPort,
            label == "Show" ? ChromeWindowMover.SwShownoactivate : ChromeWindowMover.SwHide,
            hwnd => label == "Show"
                ? ChromeWindowMover.IsWindowVisibleWin32(hwnd) && !ChromeWindowMover.IsIconicWin32(hwnd)
                : !ChromeWindowMover.IsWindowVisibleWin32(hwnd),
            label,
            debug);
        return win32Verified
            ? Outcome.Successful()
            : Outcome.Failure(new OutcomeError(
                "Chrome window could not be located or moved - it may have been closed.",
                ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Problem));
    }

    private async Task<IBrowser> ConnectAsync(IPlaywright playwright, BrowserOptions options)
        => await playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{options.ChromeDebugPort}",
            new BrowserTypeConnectOverCDPOptions { Timeout = 10_000 });

    /// <summary>The window that owns the resolved chat page; one CDP round trip.</summary>
    private static async Task<long> ResolveWindowIdAsync(ICDPSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = await session.SendAsync("Browser.getWindowForTarget");
        return ((System.Text.Json.JsonElement)response).GetProperty("windowId").GetInt64();
    }

    private static async Task<string> ResolveTargetIdAsync(ICDPSession session, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var response = await session.SendAsync("Target.getTargetInfo");
        return ((System.Text.Json.JsonElement)response)
            .GetProperty("targetInfo").GetProperty("targetId").GetString() ?? string.Empty;
    }

    private static Outcome PortClosed(BrowserOptions options)
        => Outcome.Failure(new OutcomeError(
            $"Chrome is not running on port {options.ChromeDebugPort}. Run 'keyless chrome' first.",
            ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Problem));
}
