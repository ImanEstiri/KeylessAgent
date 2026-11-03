using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Playwright;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// The ONE shared window-move engine, used by both ChromeWindowMinimizer (during a turn)
/// and ChromeWindowController (keyless show/hide, WPF buttons, login flow):
/// 1. CDP move with READ-BACK VERIFICATION: Browser.setWindowBounds, then Browser.getWindowBounds
///    must report exactly the target bounds and windowState — with a forced-normal recovery
///    between attempts, up to 3 tries. True = verified; false = CDP could not confirm.
/// 2. Win32 fallback for BOTH directions: enumerate the top-level Chrome windows of OUR
///    chrome.exe processes (the launched PID's descendant tree, or — when attached — the
///    processes whose command line carries our --user-data-dir / --remote-debugging-port)
///    and ShowWindow them, then verify the expected visibility state by reading the windows
///    back (IsWindowVisible / IsIconic). The launcher PID often has MainWindowHandle == 0,
///    which is why the naive fallback missed.
/// 3. Chat-window picking: prefer the page whose URL is the chat host, so with several tabs
///    open the WINDOW THAT OWNS THE CHAT TAB is the one that gets moved.
/// Logging flows through simple delegates so both call sites keep their own typed loggers.
/// </summary>
internal static class ChromeWindowMover
{
    internal const int SwHide = 0;
    internal const int SwShownoactivate = 4;
    internal const int SwRestore = 9;

    /// <summary>Test seam: replaces the Win32 pass (so unit tests never touch real windows).</summary>
    internal static Func<int, string, int, Func<IntPtr, bool>, string, Action<string>?, bool>? Win32ShowWindowsOverride { get; set; }

    /// <summary>
    /// One CDP move + read-back verification loop: setWindowBounds, then getWindowBounds must
    /// match the target exactly; on mismatch force normal first and retry, up to 3 attempts.
    /// </summary>
    internal static async Task<bool> TryMoveViaCdpAsync(
        ICDPSession session, long windowId, (int Left, int Top, int Width, int Height) bounds, string label,
        Action<string>? info = null, Action<string>? debug = null)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object>
                {
                    ["left"] = bounds.Left,
                    ["top"] = bounds.Top,
                    ["width"] = bounds.Width,
                    ["height"] = bounds.Height,
                    ["windowState"] = "normal",
                },
            });

            if (await ReadBackMatchesAsync(session, windowId, bounds))
            {
                debug?.Invoke($"CDP {label}: read-back verified (attempt {attempt}).");
                return true;
            }

            // Not verified: force NORMAL first (clears the bounds transition), then retry.
            debug?.Invoke($"CDP {label}: read-back mismatch (attempt {attempt}); forcing normal and retrying.");
            await SetWindowStateAsync(session, windowId, "normal");
            await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
            {
                ["windowId"] = windowId,
                ["bounds"] = new Dictionary<string, object>
                {
                    ["left"] = bounds.Left,
                    ["top"] = bounds.Top,
                    ["width"] = bounds.Width,
                    ["height"] = bounds.Height,
                    ["windowState"] = "normal",
                },
            });

            if (await ReadBackMatchesAsync(session, windowId, bounds))
            {
                debug?.Invoke($"CDP {label}: read-back verified after the recovery (attempt {attempt}).");
                return true;
            }

            await Task.Delay(300);
        }

        return false;
    }

    private static async Task SetWindowStateAsync(ICDPSession session, long windowId, string windowState)
        => await session.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
        {
            ["windowId"] = windowId,
            ["bounds"] = new Dictionary<string, object> { ["windowState"] = windowState },
        });

    /// <summary>True when the read-back bounds match the target exactly, windowState "normal".</summary>
    private static async Task<bool> ReadBackMatchesAsync(ICDPSession session, long windowId, (int Left, int Top, int Width, int Height) bounds)
    {
        var response = await session.SendAsync("Browser.getWindowBounds",
            new Dictionary<string, object> { ["windowId"] = windowId });
        var element = (System.Text.Json.JsonElement)response;
        if (!element.TryGetProperty("bounds", out var shape))
            return false;
        return shape.TryGetProperty("windowState", out var state) &&
               string.Equals(state.GetString(), "normal", StringComparison.OrdinalIgnoreCase) &&
               shape.TryGetProperty("left", out var left) && left.GetInt32() == bounds.Left &&
               shape.TryGetProperty("top", out var top) && top.GetInt32() == bounds.Top &&
               shape.TryGetProperty("width", out var width) && width.GetInt32() == bounds.Width &&
               shape.TryGetProperty("height", out var height) && height.GetInt32() == bounds.Height;
    }

    /// <summary>
    /// The Win32 pass for BOTH directions: ShowWindow our Chrome windows with
    /// <paramref name="showCommand"/>, then verify at least one window reached the expected
    /// state (<paramref name="verifyWindow"/>) within ~500 ms. True = verified.
    /// </summary>
    internal static bool ShowWindowsViaWin32(
        int launchedProcessId, string userDataDir, int debugPort, int showCommand,
        Func<IntPtr, bool> verifyWindow, string label, Action<string>? debug = null)
    {
        if (Win32ShowWindowsOverride is not null)
            return Win32ShowWindowsOverride(launchedProcessId, userDataDir, showCommand, verifyWindow, label, debug);

        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            var handles = FindChromeWindowHandles(launchedProcessId, userDataDir, debugPort);
            if (handles.Count == 0)
            {
                debug?.Invoke($"Win32 {label}: no Chrome window of our chrome.exe processes was found.");
                return false;
            }

            foreach (var handle in handles)
                ShowWindow(handle, showCommand);

            // Read-back: the visibility state must actually flip, not just be commanded.
            var verified = false;
            for (var attempt = 0; attempt < 5 && !verified; attempt++)
            {
                Thread.Sleep(100);
                verified = handles.Any(verifyWindow);
            }

            debug?.Invoke($"Win32 {label}: ShowWindow sent to {handles.Count} Chrome window(s); verified: {verified}.");
            return verified;
        }
        catch (Exception ex)
        {
            debug?.Invoke($"Win32 {label} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The window to move: prefer the page whose URL is the chat host (with several tabs open
    /// the FIRST page is often not the chat), falling back to the first page, else null.
    /// </summary>
    internal static IPage? PickChatPage(IBrowserContext context)
        => context.Pages.FirstOrDefault(p => p.Url.Contains("chat.deepseek.com", StringComparison.OrdinalIgnoreCase))
           ?? context.Pages.FirstOrDefault();

    /// <summary>All visible top-level Chrome windows (class Chrome_WidgetWin_1) of our processes.</summary>
    private static IReadOnlyList<IntPtr> FindChromeWindowHandles(int launchedProcessId, string userDataDir, int debugPort)
    {
        var ours = CollectOurChromeProcessIds(launchedProcessId, userDataDir, debugPort);
        var handles = new List<IntPtr>();
        if (ours.Count == 0)
            return handles;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisibleWin32(hwnd))
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

    /// <summary>Visibility read-back for the Win32 verification predicates.</summary>
    internal static bool IsWindowVisibleWin32(IntPtr hwnd) => IsWindowVisible(hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>Minimized-state read-back for the Win32 show verification.</summary>
    internal static bool IsIconicWin32(IntPtr hwnd) => IsIconic(hwnd);

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
