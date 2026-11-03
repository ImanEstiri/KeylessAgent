using System.Diagnostics;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Seam over OS/HTTP concerns of the browser connection: probe the debug port, locate
/// chrome.exe, and start the real Chrome. Faked in the connector tests.
/// </summary>
internal interface IBrowserEnvironment
{
    /// <summary>
    /// Whether <c>http://127.0.0.1:{port}/json/version</c> answers.
    /// </summary>
    Task<bool> IsDebugPortOpenAsync(int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locates chrome.exe: the configured path, then the registry App Paths, then the common
    /// install locations. Returns null when Chrome cannot be found.
    /// </summary>
    Task<string?> LocateChromeAsync(string configuredPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts the real Chrome with the dedicated profile and the debugging port.
    /// </summary>
    /// <param name="windowMode">Offscreen, Minimized (alias of offscreen), TinyCorner, EdgePeek or Normal.</param>
    /// <param name="onCommandLine">Receives the FULL composed command line right before Process.Start
    /// (diagnostics: 'chrome-launched').</param>
    /// <returns>The process id of the started Chrome.</returns>
    Task<int> StartChromeAsync(string chromePath, int port, string userDataDir, string windowMode, CancellationToken cancellationToken = default, Action<string>? onCommandLine = null);

    /// <summary>
    /// Connects over CDP to the Chrome on the debug port. Playwright-typed on the real
    /// implementation; faked in connector tests so no real browser is needed.
    /// </summary>
    Task<(object? Browser, object Context)> AttachCdpAsync(IPlaywright playwright, int port, CancellationToken cancellationToken = default);
}

/// <summary>
/// Real implementation: HttpClient probe, registry + file-system chrome lookup, Process.Start.
/// </summary>
internal sealed class BrowserEnvironment : IBrowserEnvironment
{
    private readonly ImanSoftware.Logging.ILoggerAdapter<BrowserEnvironment>? _log;

    public BrowserEnvironment() : this(null)
    {
    }

    internal BrowserEnvironment(ImanSoftware.Logging.ILoggerAdapter<BrowserEnvironment>? log)
    {
        _log = log;
    }

    public async Task<bool> IsDebugPortOpenAsync(int port, CancellationToken cancellationToken = default)
    {
        // A closed port must read as "port closed" (false), never as cancellation: HttpClient's
        // own timeout surfaces as TaskCanceledException (an OCE subclass) which the agent loop
        // would misreport as "cancelled by the user". Only the CALLER's token cancels.
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(
                $"http://127.0.0.1:{port}/json/version", timeoutCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // probe timeout: port closed or silently dropped
        }
        catch (OperationCanceledException)
        {
            throw; // the caller really cancelled
        }
        catch
        {
            return false;
        }
    }

    public Task<string?> LocateChromeAsync(string configuredPath, CancellationToken cancellationToken = default)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configuredPath))
            candidates.Add(configuredPath);

        // Registry: HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            if (key?.GetValue(null) is string registered && File.Exists(registered))
                candidates.Add(registered);
        }
        catch
        {
            // Registry unavailable: fall through to the file-system locations.
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
        candidates.Add(Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"));
        candidates.Add(Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"));

        return Task.FromResult(candidates.FirstOrDefault(File.Exists));
    }

    public async Task<(object? Browser, object Context)> AttachCdpAsync(IPlaywright playwright, int port, CancellationToken cancellationToken = default)
    {
        var browser = await playwright.Chromium.ConnectOverCDPAsync(
            $"http://127.0.0.1:{port}",
            new BrowserTypeConnectOverCDPOptions { Timeout = 15_000 });

        var contexts = browser.Contexts;
        if (contexts.Count == 0)
        {
            throw new InvalidOperationException(
                "The Chrome on the debug port exposes no browser context (is it still starting?).");
        }

        return (browser, contexts[0]);
    }

    public Task<int> StartChromeAsync(string chromePath, int port, string userDataDir, string windowMode, CancellationToken cancellationToken = default, Action<string>? onCommandLine = null)
    {
        var arguments = ComposeChromeArguments(port, userDataDir, windowMode);

        // Diagnostics: the full command line BEFORE Process.Start, so a turn log can prove
        // which anti-occlusion flags the launched Chrome actually received.
        _log?.LogInformation("Chrome launch command line: {Arguments}", arguments);
        onCommandLine?.Invoke(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = arguments,
            UseShellExecute = true,
        };

        using var process = Process.Start(startInfo);
        return Task.FromResult(process?.Id ?? 0);
    }

    /// <summary>
    /// The primary screen size in pixels — the same values as
    /// System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width/Height, without pulling the
    /// WinForms dependency into Core. Falls back to 1920x1080 when unavailable.
    /// </summary>
    internal static (int Width, int Height) PrimaryScreenSize()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                // SM_CXSCREEN / SM_CYSCREEN: primary screen width/height.
                var width = GetSystemMetrics(0);
                var height = GetSystemMetrics(1);
                if (width > 0 && height > 0)
                    return (width, height);
            }
            catch
            {
                // Fall through to the default.
            }
        }

        return (1920, 1080);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// The hide bounds: a VISIBLE-but-imperceptible 80x80 window in the bottom-right corner
    /// of the primary screen. The fully offscreen (-32000,-32000) position triggers Chrome's
    /// occlusion throttling (turn-17 truncated 20 times with the occlusion flag present);
    /// a tiny on-screen window does not. Used by every hide path and by WindowMode
    /// TinyCorner, so show/hide and launch all agree on one geometry.
    /// </summary>
    internal static (int Left, int Top, int Width, int Height) TinyCornerBounds((int Width, int Height)? screen = null)
    {
        var (width, height) = screen ?? PrimaryScreenSize();
        return (width - 80, height - 80, 80, 80);
    }

    /// <summary>
    /// Maps the window mode to its Chrome flags. Minimized and Offscreen place the window
    /// fully offscreen; TinyCorner and EdgePeek keep a VISIBLE-but-tiny window in the
    /// screen's bottom-right corner — Chrome treats fully offscreen windows as occluded and
    /// throttles the renderer regardless of the flags (turn-17/18), so a tiny on-screen
    /// window is the reliable anti-throttling shape. "Minimized" is kept for backward
    /// compatibility with existing keyless.json files but now means offscreen. Normal → no
    /// flag (never forced focus). Unknown or empty modes default to offscreen.
    /// </summary>
    internal static string WindowModeFlag(string windowMode, int screenWidth, int screenHeight)
        => windowMode.Trim().ToLowerInvariant() switch
        {
            "normal" => "",
            "tinycorner" => $"--window-position={screenWidth - 80},{screenHeight - 80} --window-size=80,80",
            "edgepeek" => $"--window-position={screenWidth - 10},{screenHeight - 10} --window-size=10,10",
            // Minimized is kept for backward compatibility: it means offscreen as well.
            _ => "--window-position=-32000,-32000 --window-size=1280,900",
        };

    internal static string ComposeChromeArguments(int port, string userDataDir, string windowMode, (int Width, int Height)? screen = null)
    {
        var (screenWidth, screenHeight) = screen ?? PrimaryScreenSize();
        var windowFlag = WindowModeFlag(windowMode, screenWidth, screenHeight);
        var windowPrefix = windowFlag.Length > 0 ? windowFlag + " " : string.Empty;

        // NO URL is passed: Chrome 136+ ignores --start-minimized when a URL is present
        // (the window must be visible to load it). The chat tab is opened via CDP after
        // the attach, so the launched window can start minimized.
        return windowPrefix +
            $"--remote-debugging-port={port} --remote-debugging-address=127.0.0.1 " +
            $"--user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check " +
            "--disable-renderer-backgrounding --disable-background-timer-throttling " +
            "--disable-backgrounding-occluded-windows " +
            // Windows 11 throttles a MINIMIZED window through native occlusion detection
            // even with the flags above, which truncates the DeepSeek stream mid-reply:
            // skip occlusion detection and intensive wake-up throttling at the feature level.
            "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling " +
            "--disable-ipc-flooding-protection --disable-hang-monitor";
    }
}
