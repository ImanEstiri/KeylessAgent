using System.IO;
using System.Windows;
using ImanSoftware.KeylessAgent.Core.Factory;
using ImanSoftware.KeylessAgent.Wpf.Services;

namespace ImanSoftware.KeylessAgent.Wpf;

/// <summary>
/// WPF entry point of the KeylessAgent desktop UI with global crash handling (Appendix E):
/// every unhandled exception is appended to <c>%LOCALAPPDATA%\KeylessAgent\wpf-crash.log</c>
/// and reported through the dialog host when it exists — the app keeps running instead of
/// closing silently.
/// </summary>
public partial class App : Application
{
    /// <summary>Initializes the global exception handlers.</summary>
    /// <param name="e">The startup arguments.</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            ShowErrorOnUi(args.Exception.Message);
            args.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog(args.ExceptionObject as Exception
                ?? new InvalidOperationException($"Non-exception failure: {args.ExceptionObject}"));

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// ALWAYS-ON safety net for the OS-level Chrome the app itself launched. DISTINCTION
    /// from Browser.CloseChromeOnExit (do NOT merge the two): CloseChromeOnExit only gates
    /// PlaywrightChatProvider.DisposeAsync - whether the chat TAB/CONTEXT stays open between
    /// turns while the app is running. This override instead closes the actual Chrome
    /// process/window on normal application exit, because the TinyCorner window is invisible
    /// by design and would otherwise outlive the app unnoticed. Best-effort with a hard
    /// timeout; never blocks shutdown indefinitely; never lets an exception escape.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        // EVERY outcome is logged to the crash-log file (%LOCALAPPDATA%\KeylessAgent\
        // wpf-crash.log), which survives shutdown — so a silent failure is debuggable from
        // the log alone without external Task Manager checks.
        try
        {
            // The close runs on a worker (CloseAsync is async; production code must never
            // block on tasks - the NoSyncOverAsync rule). Shutdown is bounded by a
            // ManualResetEventSlim wait (a plain synchronization primitive, NOT Task.Wait).
            // BUDGET: the worker's own cancellation allows 6s and the wait allows 6.5s —
            // deliberately LONGER than the close internals (graceful race 2.5s + confirm
            // poll ~1.5s + kill poll ~1.5s) so the wait can never truncate a close that is
            // still making progress. The old 4s == 4s pairing was the flaky race.
            using var completed = new System.Threading.ManualResetEventSlim(false);
            var workerStatus = "worker did not finish within the shutdown wait";
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var controller = KeylessAgentServiceFactory.CreateChromeWindowController();
                    WriteCrashLog("chrome-exit: controller created");

                    // Ownership matches the RUNNING processes' command lines against the
                    // options of the LAST successful connect (ChromeLaunchTracker) — falling
                    // back to defaults when this app run never connected.
                    var options = Core.Implementation.Browser.ChromeLaunchTracker.Last
                        ?? new Abstractions.Models.Configuration.BrowserOptions();
                    WriteCrashLog($"chrome-exit: closing port={options.ChromeDebugPort} " +
                        $"profile={Core.Implementation.Browser.ChromeLaunchTracker.LastUserDataDir}");

                    await controller.CloseAsync(options, allowAttached: false, cts.Token);
                    WriteCrashLog("chrome-exit: CloseAsync completed");

                    // Explicit confirmation within the remaining budget: the log must state
                    // definitively whether the port went down on this exact run.
                    var confirmWatch = System.Diagnostics.Stopwatch.StartNew();
                    while (confirmWatch.Elapsed < TimeSpan.FromSeconds(2)
                           && !cts.IsCancellationRequested)
                    {
                        if (!await controller.IsDebugPortOpenAsync(options, cts.Token))
                        {
                            WriteCrashLog("chrome-exit: confirmed — the debug port is closed");
                            return;
                        }

                        await Task.Delay(200, cts.Token);
                    }

                    WriteCrashLog("chrome-exit: the debug port was STILL OPEN after the close");
                }
                catch (OperationCanceledException)
                {
                    WriteCrashLog("chrome-exit: the 6s budget was exhausted before the close finished");
                }
                catch (Exception ex)
                {
                    WriteCrashLog($"chrome-exit: CloseAsync threw {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    completed.Set();
                }
            });

            if (!completed.Wait(TimeSpan.FromSeconds(6.5)))
            {
                WriteCrashLog("chrome-exit: the shutdown wait timed out after 6.5s — " +
                    "the close worker was still running when the process exited");
                workerStatus = "wait timed out after 6.5s";
            }
            else
            {
                workerStatus = "worker finished";
            }

            WriteCrashLog("chrome-exit: " + workerStatus);
        }
        catch (Exception ex)
        {
            // Chrome cleanup must never make application shutdown fail or hang.
            WriteCrashLog("chrome-exit: OnExit itself threw " + ex.GetType().Name + ": " + ex.Message);
        }

        base.OnExit(e);
    }

    /// <summary>The crash-log directory (<c>%LOCALAPPDATA%\KeylessAgent</c> by default).</summary>
    /// <param name="baseDirectory">Overrides the base directory (used by tests).</param>
    internal static string GetCrashLogDirectory(string? baseDirectory = null)
        => Path.Combine(baseDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeylessAgent");

    /// <summary>The full crash-log path (<c>wpf-crash.log</c> inside the crash-log directory).</summary>
    /// <param name="baseDirectory">Overrides the base directory (used by tests).</param>
    internal static string GetCrashLogPath(string? baseDirectory = null)
        => Path.Combine(GetCrashLogDirectory(baseDirectory), "wpf-crash.log");

    /// <summary>Appends one exception to the crash log. Never throws (guide Appendix E).</summary>
    /// <param name="exception">The exception to record.</param>
    /// <param name="baseDirectory">Overrides the base directory (used by tests).</param>
    internal static void WriteCrashLog(Exception exception, string? baseDirectory = null)
        => WriteCrashLogLine(exception?.ToString() ?? "null exception", baseDirectory);

    /// <summary>
    /// Appends a single diagnostic line to the crash log. Used by the shutdown Chrome-close
    /// safety net, which runs AFTER normal logging may already be torn down — the crash log
    /// file survives shutdown, so late diagnostics stay readable.
    /// </summary>
    internal static void WriteCrashLog(string message, string? baseDirectory = null)
        => WriteCrashLogLine(message, baseDirectory);

    private static void WriteCrashLogLine(string line, string? baseDirectory)
    {
        try
        {
            var entry = $"{DateTime.UtcNow:O} {line}{Environment.NewLine}{Environment.NewLine}";
            Directory.CreateDirectory(GetCrashLogDirectory(baseDirectory));
            File.AppendAllText(GetCrashLogPath(baseDirectory), entry);
        }
        catch
        {
            // Crash logging must never throw; the failure is dropped deliberately.
        }
    }

    private static void ShowErrorOnUi(string message)
    {
        var dispatcher = Current?.Dispatcher;
        if (dispatcher is null)
            return;

        _ = dispatcher.BeginInvoke(() =>
        {
            if (!DialogHost.HasError)
                DialogHost.ShowError(message);
        });
    }
}
