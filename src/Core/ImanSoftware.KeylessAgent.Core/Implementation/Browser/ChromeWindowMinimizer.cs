using System.Diagnostics;
using System.Runtime.InteropServices;
using ImanSoftware.Logging;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// Keeps the chat window out of the user's way WITHOUT throttling Chrome:
/// the window is MOVED offscreen (-32000,-32000, 1280x900) with windowState "normal" —
/// invisible on every monitor, but Chrome's layout engine still considers it visible, so
/// the renderer keeps running at full speed and streaming replies arrive complete. A truly
/// minimized window (windowState "minimized") is throttled by the renderer and truncates
/// the DeepSeek stream — never used anymore.
/// 1. CDP: Browser.setWindowBounds(offscreen, normal), then the state is READ BACK via
///    Browser.getWindowBounds — Chrome occasionally ignores the first move, so a
///    normal→offscreen recovery runs, up to 3 attempts.
/// 2. Win32 fallback (CDP failed, timed out, or never verified): enumerate the top-level
///    windows of OUR chrome.exe processes — the launched PID's descendant tree, or (when
///    attached to an already-running Chrome) the processes whose command line carries our
///    --user-data-dir / --remote-debugging-port — and ShowWindow(SW_HIDE) each (hiding,
///    not minimizing: SW_MINIMIZE would throttle the renderer). The launcher PID often has
///    MainWindowHandle == 0, which is why the naive fallback missed.
/// Best-effort everywhere: a failure is logged and never fatal. Windows-only guards guard
/// every native call. Recover the window with 'keyless show' or the WPF Show Chrome button.
/// </summary>
internal static class ChromeWindowMinimizer
{
    private const int SwHide = 0;

    internal static async Task HideAsync(
        IPage page, int launchedProcessId, string userDataDir, int debugPort,
        ILoggerAdapter<PlaywrightChatProvider>? log, CancellationToken cancellationToken)
    {
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
                    for (var attempt = 1; attempt <= 3; attempt++)
                    {
                        await MoveOffscreenAsync(session, windowId);
                        if (await IsOffscreenAsync(session, windowId))
                            return true;

                        // Not verified: force NORMAL first (clears the bounds transition),
                        // then offscreen again, and read back once more on the next attempt.
                        await SetWindowStateAsync(session, windowId, "normal");
                        await MoveOffscreenAsync(session, windowId);
                        if (await IsOffscreenAsync(session, windowId))
                            return true;
                        await Task.Delay(300);
                    }

                    return false;
                }, cdpCts.Token);

                var completed = await Task.WhenAny(cdpWork, Task.Delay(Timeout.Infinite, cdpCts.Token));
                if (completed != cdpWork)
                    throw new OperationCanceledException(cdpCts.Token);

                verified = await cdpWork;
                if (verified)
                    return; // CDP confirmed the window is offscreen.
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
            log?.LogDebug("CDP hide failed: {Message}; falling back to Win32.", ex.Message);
        }

        if (!verified)
            log?.LogDebug("CDP hide was not verified; falling back to Win32.");

        HideWindowsWin32(launchedProcessId, userDataDir, debugPort, log);
    }

    /// <summary>The hide move: offscreen, still "normal" — user cannot see it, Chrome does not throttle it.</summary>
    private static async Task MoveOffscreenAsync(ICDPSession session, long windowId)
    {
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

    private static async Task SetWindowStateAsync(ICDPSession session, long windowId, string windowState)
        => await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
        {
            ["windowId"] = windowId,
            ["bounds"] = new Dictionary<string, object> { ["windowState"] = windowState },
        });

    /// <summary>True when the read-back bounds say offscreen (left/top -32000) and "normal".</summary>
    private static async Task<bool> IsOffscreenAsync(ICDPSession session, long windowId)
    {
        var response = await session.SendAsync("Browser.getWindowBounds",
            new Dictionary<string, object> { ["windowId"] = windowId });
        var bounds = ((System.Text.Json.JsonElement)response);
        if (!bounds.TryGetProperty("bounds", out var shape))
            return false;
        return shape.TryGetProperty("windowState", out var state) &&
               string.Equals(state.GetString(), "normal", StringComparison.OrdinalIgnoreCase) &&
               shape.TryGetProperty("left", out var left) && left.GetInt32() == -32000 &&
               shape.TryGetProperty("top", out var top) && top.GetInt32() == -32000;
    }

    private static void HideWindowsWin32(int launchedProcessId, string userDataDir, int debugPort, ILoggerAdapter<PlaywrightChatProvider>? log)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var handles = FindChromeWindowHandles(launchedProcessId, userDataDir, debugPort);
            if (handles.Count == 0)
            {
                log?.LogDebug("Win32 hide: no Chrome window of our chrome.exe processes was found.");
                return;
            }

            foreach (var handle in handles)
                ShowWindow(handle, SwHide);
            log?.LogDebug("Win32 hide: ShowWindow(SW_HIDE) sent to {Count} Chrome window(s).", handles.Count);
        }
        catch (Exception ex)
        {
            // Hide is best-effort; a failure here is never fatal.
            log?.LogDebug("Win32 hide failed: {Message}", ex.Message);
        }
    }

    /// <summary>All visible top-level Chrome windows (class Chrome_WidgetWin_1) of our processes.</summary>
    private static IReadOnlyList<IntPtr> FindChromeWindowHandles(int launchedProcessId, string userDataDir, int debugPort)
    {
        var ours = CollectOurChromeProcessIds(launchedProcessId, userDataDir, debugPort);
        var handles = new List<IntPtr>();
        if (ours.Count == 0)
            return handles;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;
            if (!ChromeClassName(GetWindowClassName(hwnd)))
                return true;
            if (GetWindowThreadProcessId(hwnd, out var pid) == 0)
                return true;
            if (!ours.Contains((int)pid))
                return true;
            handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return handles;
    }

    private static bool ChromeClassName(string className)
        => string.Equals(className, "Chrome_WidgetWin_1", StringComparison.Ordinal);

    /// <summary>
    /// The chrome.exe processes that belong to THIS agent: the launched PID's descendant
    /// tree, plus — for the attached case, where no PID is known — the processes whose
    /// command line carries our debug port or profile directory.
    /// </summary>
    private static HashSet<int> CollectOurChromeProcessIds(int launchedProcessId, string userDataDir, int debugPort)
    {
        var ours = new HashSet<int>();
        try
        {
            var chromeProcesses = new List<(int Pid, int ParentPid, string? CommandLine)>();
            foreach (var process in Process.GetProcessesByName("chrome"))
            {
                try
                {
                    chromeProcesses.Add((process.Id, GetParentProcessId(process.Id), TryReadCommandLine(process.Id)));
                }
                catch
                {
                    // A dying process between enumeration and query is normal.
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (launchedProcessId > 0)
            {
                // Descendant tree of the launched launcher (the browser process we started).
                var children = chromeProcesses
                    .Where(p => p.ParentPid > 0)
                    .ToLookup(p => p.ParentPid, p => p.Pid);
                var queue = new Queue<int>();
                queue.Enqueue(launchedProcessId);
                ours.Add(launchedProcessId);
                while (queue.Count > 0)
                {
                    foreach (var child in children[queue.Dequeue()])
                        if (ours.Add(child))
                            queue.Enqueue(child);
                }
            }

            foreach (var (pid, _, commandLine) in chromeProcesses)
            {
                if (commandLine is null)
                    continue;
                if (commandLine.Contains($"--remote-debugging-port={debugPort}", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(userDataDir) &&
                     commandLine.Contains(userDataDir, StringComparison.OrdinalIgnoreCase)))
                {
                    ours.Add(pid);
                }
            }
        }
        catch
        {
            // Best-effort: returning what we have is always acceptable.
        }

        return ours;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    // --- Win32 interop (Windows-only; every caller is guarded) ---

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, [Out] char[] className, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, [Out] byte[] buffer, IntPtr size, out IntPtr read);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr process, int informationClass, ref ProcessBasicInformation info, int length, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    /// <summary>The parent process id of <paramref name="processId"/>, or 0 when unavailable.</summary>
    private static int GetParentProcessId(int processId)
    {
        try
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
            if (handle == IntPtr.Zero)
                return 0;
            try
            {
                var info = new ProcessBasicInformation();
                if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                    return 0;
                return (int)info.InheritedFromUniqueProcessId;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The command line of a process read through its PEB (x64 only — the layout differs
    /// under WoW64 and this fallback does not need it); null when access is denied.
    /// </summary>
    private static string? TryReadCommandLine(int processId)
    {
        if (!Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem)
            return null;

        try
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, (uint)processId);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var info = new ProcessBasicInformation();
                if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                    return null;

                // PEB.ProcessParameters sits at 0x20 on x64; RTL_USER_PROCESS_PARAMETERS.CommandLine at 0x70.
                var parameters = ReadInt64(handle, (long)info.PebBaseAddress + 0x20);
                if (parameters == 0)
                    return null;

                var commandLine = ReadBytes(handle, parameters + 0x70, 16);
                var length = BitConverter.ToUInt16(commandLine, 0);
                var buffer = BitConverter.ToInt64(commandLine, 8);
                if (length <= 0 || buffer == 0)
                    return null;

                var raw = ReadBytes(handle, buffer, length);
                return System.Text.Encoding.Unicode.GetString(raw, 0, raw.Length);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    private static long ReadInt64(IntPtr process, long address)
    {
        var raw = ReadBytes(process, address, 8);
        return BitConverter.ToInt64(raw, 0);
    }

    private static byte[] ReadBytes(IntPtr process, long address, int count)
    {
        var buffer = new byte[count];
        if (!ReadProcessMemory(process, (IntPtr)address, buffer, (IntPtr)count, out var read) || read.ToInt64() != count)
            throw new InvalidOperationException($"ReadProcessMemory failed at 0x{address:X}.");
        return buffer;
    }
}
