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
        if (!await _environment.IsDebugPortOpenAsync(options.ChromeDebugPort, ct))
            return PortClosed(options);

        try
        {
            var playwright = await PlaywrightFactory(ct);
            var browser = await ConnectAsync(playwright, options);
            var context = browser.Contexts.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "The Chrome on the debug port exposes no browser context (is it still starting?).");
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

            var session = await page.Context.NewCDPSessionAsync(page);
            try
            {
                var windowId = await ResolveWindowIdAsync(session, page, ct);

                _log.LogInformation(
                    "CDP Show: Browser.setWindowBounds(windowId {WindowId}, bounds {{left: 100, top: 100, width: 1280, height: 900, windowState: normal}})",
                    windowId);
                await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
                {
                    ["windowId"] = windowId,
                    ["bounds"] = new Dictionary<string, object>
                    {
                        ["left"] = 100,
                        ["top"] = 100,
                        ["width"] = 1280,
                        ["height"] = 900,
                        ["windowState"] = "normal",
                    },
                });

                var targetId = await ResolveTargetIdAsync(session, ct);
                _log.LogInformation("CDP Show: Target.activateTarget(targetId {TargetId})", targetId);
                await session.SendAsync("Target.activateTarget", new Dictionary<string, object>
                {
                    ["targetId"] = targetId,
                });

                if (peekSeconds > 0)
                {
                    await Task.Delay(peekSeconds * 1000, ct);
                    await HideWindowAsync(session, windowId);
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
            return Outcome.Failure(new OutcomeError(ex));
        }
    }

    /// <inheritdoc />
    public async Task<Outcome> HideAsync(BrowserOptions options, CancellationToken ct = default)
    {
        if (!await _environment.IsDebugPortOpenAsync(options.ChromeDebugPort, ct))
            return PortClosed(options);

        try
        {
            var playwright = await PlaywrightFactory(ct);
            var browser = await ConnectAsync(playwright, options);
            var context = browser.Contexts.FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "The Chrome on the debug port exposes no browser context (is it still starting?).");
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();

            var session = await page.Context.NewCDPSessionAsync(page);
            try
            {
                var windowId = await ResolveWindowIdAsync(session, page, ct);
                await HideWindowAsync(session, windowId);
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
            return Outcome.Failure(new OutcomeError(ex));
        }
    }

    /// <summary>The hide move: offscreen, still "normal" — minimized windows can be throttled, moved ones do not.</summary>
    private async Task HideWindowAsync(ICDPSession session, long windowId)
    {
        _log.LogInformation(
            "CDP Hide: Browser.setWindowBounds(windowId {WindowId}, bounds {{left: -32000, top: -32000, width: 1280, height: 900, windowState: normal}})",
            windowId);
        await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
        {
            ["windowId"] = windowId,
            ["bounds"] = new Dictionary<string, object>
            {
                ["left"] = -32000,
                ["top"] = -32000,
                ["width"] = 1280,
                ["height"] = 900,
                ["windowState"] = "normal",
            },
        });
    }

    private async Task<IBrowser> ConnectAsync(IPlaywright playwright, BrowserOptions options)
        => await playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{options.ChromeDebugPort}",
            new BrowserTypeConnectOverCDPOptions { Timeout = 10_000 });

    /// <summary>The window that owns the chat tab (the first page); one CDP round trip.</summary>
    private static async Task<long> ResolveWindowIdAsync(ICDPSession session, IPage page, CancellationToken ct)
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
