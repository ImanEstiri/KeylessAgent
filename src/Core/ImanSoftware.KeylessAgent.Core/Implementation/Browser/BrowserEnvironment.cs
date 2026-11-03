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
    /// <param name="windowMode">Minimized, Offscreen or Normal; unknown/empty defaults to Minimized.</param>
    /// <returns>The process id of the started Chrome.</returns>
    Task<int> StartChromeAsync(string chromePath, int port, string userDataDir, string windowMode, CancellationToken cancellationToken = default);

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

    public Task<int> StartChromeAsync(string chromePath, int port, string userDataDir, string windowMode, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = chromePath,
            Arguments = ComposeChromeArguments(port, userDataDir, windowMode),
            UseShellExecute = true,
        };

        using var process = Process.Start(startInfo);
        return Task.FromResult(process?.Id ?? 0);
    }

    /// <summary>
    /// Maps the window mode to its Chrome flags. Minimized and Offscreen BOTH place the
    /// window offscreen at startup — a truly minimized window is throttled by the renderer
    /// and truncates DeepSeek streaming replies, so "Minimized" is kept for backward
    /// compatibility with existing keyless.json files but now means offscreen. Normal → no
    /// flag (never forced focus). Unknown or empty modes default to offscreen, matching the
    /// configuration default.
    /// </summary>
    internal static string WindowModeFlag(string windowMode) => windowMode.Trim().ToLowerInvariant() switch
    {
        "normal" => "",
        // Minimized is kept for backward compatibility: it now means offscreen as well.
        _ => "--window-position=-32000,-32000 --window-size=1280,900",
    };

    internal static string ComposeChromeArguments(int port, string userDataDir, string windowMode)
    {
        var windowFlag = WindowModeFlag(windowMode);
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
