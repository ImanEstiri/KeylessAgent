using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using System.Diagnostics;
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

    /// <summary>
    /// Moves the chat window to the tiny corner square (bottom-right of the work area,
    /// windowState normal) — invisible-but-on-screen, so Chrome does not occlusion-throttle it.
    /// </summary>
    Task<Outcome> HideAsync(BrowserOptions options, CancellationToken ct = default);

    /// <summary>
    /// Probes the CURRENT Chrome state WITHOUT touching anything: whether the debug port is
    /// open, whether the chrome.exe processes on it carry OUR profile directory (ownership),
    /// and how many of our processes are running.
    /// </summary>
    Task<ChromeProfileState> GetStateAsync(BrowserOptions options, CancellationToken ct = default);

    /// <summary>Whether the debug port is currently answering (read-only probe).</summary>
    Task<bool> IsDebugPortOpenAsync(BrowserOptions options, CancellationToken ct = default);

    /// <summary>
    /// CLOSES the KeylessAgent Chrome entirely (all tabs, the whole process tree) so the
    /// invisible tiny-corner window cannot outlive the session: a graceful CDP Browser.close
    /// first (flushes the profile), the debug port polled to confirm it went down, then -
    /// only if that failed - a process-tree kill of every chrome.exe whose command line
    /// carries OUR profile directory. A Chrome matched only by the debug port (started by
    /// the USER on our port) is refused unless <paramref name="allowAttached"/> - it is the
    /// user's own browser, matching the AttachedToRunningChrome ownership rule.
    /// </summary>
    /// <param name="options">Carries ChromeDebugPort and the profile dir - ownership is
    /// matched from the RUNNING processes' command lines, so no session-scoped PID is needed
    /// (works after an app restart).</param>
    /// <param name="allowAttached">Also close a Chrome that merely sits on our debug port
    /// without the KeylessAgent profile. Default false (refuse).</param>
    Task<Outcome> CloseAsync(BrowserOptions options, bool allowAttached = false, CancellationToken ct = default);
}

/// <summary>The observed Chrome state for one BrowserOptions (port + profile).</summary>
public sealed record ChromeProfileState(
    bool DebugPortOpen,
    bool OwnedByAgent,
    int ProcessCount)
{
    /// <summary>DebugPortOpen AND the processes carry OUR profile directory.</summary>
    public bool Active => DebugPortOpen && OwnedByAgent;
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

    /// <inheritdoc />
    public async Task<bool> IsDebugPortOpenAsync(BrowserOptions options, CancellationToken ct = default)
        => await _environment.IsDebugPortOpenAsync(options.ChromeDebugPort, ct);

    /// <inheritdoc />
    public async Task<ChromeProfileState> GetStateAsync(BrowserOptions options, CancellationToken ct = default)
    {
        var debugPortOpen = await _environment.IsDebugPortOpenAsync(options.ChromeDebugPort, ct);
        if (!debugPortOpen)
            return new ChromeProfileState(DebugPortOpen: false, OwnedByAgent: false, ProcessCount: 0);

        var userDataDir = BrowserConnector.ResolveUserDataDir(options);
        var infos = ProcessInfosOverride is not null
            ? ProcessInfosOverride()
            : ChromeWindowMover.GetChromeProcessInfos();
        var owned = infos
            .Where(p => p.CommandLine is not null
                && p.CommandLine.Contains(userDataDir, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // The port may be held by a foreign Chrome (no KeylessAgent profile in any command line).
        return new ChromeProfileState(
            DebugPortOpen: true,
            OwnedByAgent: owned.Count > 0,
            ProcessCount: owned.Count);
    }

    /// <inheritdoc />
    public async Task<Outcome> CloseAsync(BrowserOptions options, bool allowAttached = false, CancellationToken ct = default)
    {
        var debugPort = options.ChromeDebugPort;
        var userDataDir = BrowserConnector.ResolveUserDataDir(options);
        var info = (Action<string>)(message => _log.LogInformation("{Message}", message));
        var debug = (Action<string>)(message => _log.LogDebug("{Message}", message));

        // --- Ownership: match the RUNNING processes' command lines, never a session PID ---
        var processInfos = ProcessInfosOverride is not null
            ? ProcessInfosOverride()
            : ChromeWindowMover.GetChromeProcessInfos();
        var oursByProfile = processInfos
            .Where(p => p.CommandLine is not null && p.CommandLine.Contains(userDataDir, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Pid)
            .ToList();
        var oursByPortOnly = processInfos
            .Where(p => p.CommandLine is not null
                && p.CommandLine.Contains($"--remote-debugging-port={debugPort}", StringComparison.OrdinalIgnoreCase)
                && (p.CommandLine is null || !p.CommandLine.Contains(userDataDir, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Pid)
            .Except(oursByProfile)
            .ToList();

        if (oursByProfile.Count == 0 && oursByPortOnly.Count == 0)
        {
            var portOpen = await _environment.IsDebugPortOpenAsync(debugPort, ct);
            return Outcome.Failure(new OutcomeError(
                portOpen
                    ? $"Chrome is running on port {debugPort} but it does not carry the KeylessAgent profile and was not launched by KeylessAgent - nothing was closed."
                    : $"Chrome is not running on port {debugPort} - nothing to close.",
                ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Problem));
        }

        if (oursByProfile.Count == 0 && !allowAttached)
        {
            // The ownership rule of DisposeAsync/AttachedToRunningChrome: a Chrome sitting on
            // our port without OUR profile is the user's own browser - never silently killed.
            return Outcome.Failure(new OutcomeError(
                $"The Chrome on port {debugPort} was not launched with the KeylessAgent profile, so it looks like your own browser. It was NOT closed.",
                ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Problem));
        }

        var killTargets = oursByProfile.ToList();
        if (allowAttached)
            killTargets.AddRange(oursByPortOnly);

        // --- 1. Graceful: CDP Browser.close (flushes the profile, closes every tab) ---
        // RACED against a 2.5s sub-budget: Playwright.CreateAsync + ConnectOverCDPAsync can
        // be slow (driver spawn, AV scanning) and an unbounded graceful phase is what made
        // the shutdown close flaky. If the race is lost we fall through to the kill
        // fallback, which is fast and deterministic. The abandoned task is orphaned on
        // purpose: it gets GC'd / fails harmlessly when the process or browser goes away.
        try
        {
            if (await _environment.IsDebugPortOpenAsync(debugPort, ct))
            {
                var graceful = Task.Run(async () =>
                {
                    var playwright = await PlaywrightFactory(ct);
                    var browser = await ConnectAsync(playwright, options);
                    var context = browser.Contexts.FirstOrDefault()
                        ?? throw new InvalidOperationException(
                            "The Chrome on the debug port exposes no browser context (is it still starting?).");
                    var page = ChromeWindowMover.PickChatPage(context) ?? await context.NewPageAsync();
                    var session = await page.Context.NewCDPSessionAsync(page);
                    try
                    {
                        info("CDP Close: Browser.close (graceful, whole browser).");
                        await session.SendAsync("Browser.close", new Dictionary<string, object>());
                    }
                    finally
                    {
                        try { await session.DetachAsync(); }
                        catch { /* the browser may already be gone - that is the goal */ }
                    }
                }, CancellationToken.None); // orphan on budget expiry instead of cancelling mid-close

                var gracefulWon = await Task.WhenAny(graceful, Task.Delay(2_500, ct)) == graceful;
                if (gracefulWon)
                {
                    try { await graceful; }
                    catch (Exception ex) { debug($"graceful close threw: {ex.Message}"); }
                }
                else
                {
                    debug("graceful phase exceeded its 2.5s sub-budget; falling back to the kill fallback.");
                }
            }
        }
        catch (Exception ex)
        {
            debug($"CDP close failed: {ex.Message}; falling back to process termination.");
        }

        // --- 2. Confirm the port actually went down (bounded: 5 x 300 ms) ---
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!await _environment.IsDebugPortOpenAsync(debugPort, ct))
            {
                info("Chrome closed: the debug port is down (graceful close confirmed).");
                return Outcome.Successful();
            }

            await Task.Delay(300, ct);
        }

        // --- 3. Fallback: kill the whole process tree of every owned root ---
        debug("The debug port stayed open after Browser.close; killing the owned process trees.");
        foreach (var pid in killTargets.Distinct())
        {
            try
            {
                if (KillOverride is not null)
                {
                    KillOverride(pid); // test seam: no real process is ever killed from a unit test
                    continue;
                }

                using var process = Process.GetProcessById(pid);
                if (process is null || process.HasExited)
                    continue;
                info($"Killing chrome.exe process tree (pid {pid}).");
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                debug($"Kill of pid {pid} skipped: {ex.Message}"); // already exited - fine
            }
        }

        // --- 4. Confirm: bounded poll (5 x 300 ms) for port down AND processes gone ---
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var anyLeft = (ProcessInfosOverride is not null ? ProcessInfosOverride() : ChromeWindowMover.GetChromeProcessInfos())
                .Any(p => killTargets.Contains(p.Pid));
            if (!await _environment.IsDebugPortOpenAsync(debugPort, ct) && !anyLeft)
            {
                info("Chrome closed: the process tree is gone.");
                return Outcome.Successful();
            }

            await Task.Delay(300, ct);
        }

        return Outcome.Failure(new OutcomeError(
            $"Chrome could not be closed on port {debugPort} - the process tree survived the graceful close and the kill. Close it manually.",
            ErrorCodes.BrowserConnectionFailed, OutcomeErrorType.Failure));
    }

    /// <summary>Test seam: replaces the real chrome.exe enumeration (unit tests must never touch live processes).</summary>
    internal Func<List<(int Pid, int ParentPid, string? CommandLine)>>? ProcessInfosOverride { get; set; }

    /// <summary>Test seam: replaces Process.Kill (unit tests must never kill real processes).</summary>
    internal Action<int>? KillOverride { get; set; }

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
