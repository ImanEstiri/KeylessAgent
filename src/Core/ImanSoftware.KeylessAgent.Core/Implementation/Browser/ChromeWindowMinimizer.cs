
















using ImanSoftware.Logging;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Keeps the chat window out of the user's way WITHOUT throttling Chrome, during a turn:
/// the window is MOVED to a VISIBLE-but-imperceptible 80x80 square in the bottom-right
/// corner of the primary screen's WORK AREA (windowState "normal"). The fully offscreen
/// (-32000,-32000) position turned out to be OCCLUDED in Chrome's eyes — the renderer
/// throttled and the DeepSeek stream truncated even with the occlusion feature disabled
/// (turn-17: 20 truncations; turn-18: visible window succeeded, hidden again truncated) —
/// so the hide geometry is tiny-but-on-screen. A truly minimized window is equally
/// throttled and never used.
/// The move mechanics (CDP read-back verification + the Win32 fallbacks for both directions)
/// live in <see cref="ChromeWindowMover"/>; this class only orchestrates them with the
/// five-second CDP race. Recover the window with 'keyless show' or the WPF Show button.
/// </summary>
internal static class ChromeWindowMinimizer
{
    internal static async Task HideAsync(
        IPage page, int launchedProcessId, string userDataDir, int debugPort,
        ILoggerAdapter<PlaywrightChatProvider>? log, CancellationToken cancellationToken)
    {
        var bounds = BrowserEnvironment.TinyCornerBounds();
        var info = log is null ? (Action<string>?)null : message => log.LogInformation("{Message}", message);
        var debug = log is null ? (Action<string>?)null : message => log.LogDebug("{Message}", message);

        // The CDP block gets its own five-second window: a hung CDP call falls through to
        // the Win32 fallback instead of running until the caller's cancellation.
        using var cdpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cdpCts.CancelAfter(TimeSpan.FromSeconds(5));
        var verified = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await page.Context.NewCDPSessionAsync(page);
            try
            {
                // ICDPSession.SendAsync has no CancellationToken overload, so the whole CDP
                // exchange is raced against the five-second window; a hung call loses the
                // race and the Win32 fallback takes over.
                var cdpWork = Task.Run(async () =>
                {
                    var window = await session.SendAsync("Browser.getWindowForTarget");
                    var windowId = ((System.Text.Json.JsonElement)window).GetProperty("windowId").GetInt64();
                    return await ChromeWindowMover.TryMoveViaCdpAsync(session, windowId, bounds, "Hide", info, debug);
                }, cdpCts.Token);

                var completed = await Task.WhenAny(cdpWork, Task.Delay(Timeout.Infinite, cdpCts.Token));
                if (completed != cdpWork)
                    throw new OperationCanceledException(cdpCts.Token);

                verified = await cdpWork;
                if (verified)
                    return; // CDP confirmed the window sits in the tiny corner.
            }
            catch (OperationCanceledException) when (cdpCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // fall through to the Win32 fallback
            }
            finally
            {
                await session.DetachAsync();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            debug?.Invoke($"CDP hide failed: {ex.Message}; falling back to Win32.");
        }

        if (!verified)
            debug?.Invoke("CDP hide was not verified; falling back to Win32.");

        // Best-effort, as before: the turn never fails because the window could not be moved.
        // The fallback MOVES the window to the tiny corner and keeps it VISIBLE — it must
        // NEVER call ShowWindow(SW_HIDE), which would make Chrome occlusion-throttle it.
        ChromeWindowMover.MoveWindowsViaWin32(
            launchedProcessId, userDataDir, debugPort,
            (bounds.Left, bounds.Top), "Hide", debug);
    }
}
