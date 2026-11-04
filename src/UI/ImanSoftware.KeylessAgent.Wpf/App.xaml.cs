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
        try
        {
            // The close runs on a worker (CloseAsync is async; production code must never
            // block on tasks - the NoSyncOverAsync rule). Shutdown is bounded by a
            // ManualResetEventSlim wait (a plain synchronization primitive, NOT Task.Wait)
            // for the same 4s the worker's own cancellation token allows, so the process
            // never lingers longer than that even if Chrome is unresponsive.
            using var completed = new System.Threading.ManualResetEventSlim(false);
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
                    var controller = KeylessAgentServiceFactory.CreateChromeWindowController();

                    // Ownership comes from the RUNNING processes' command lines (profile dir +
                    // debug port in BrowserOptions), so the default options suffice even
                    // without a selected workspace; allowAttached=false never touches a
                    // user-launched Chrome.
                    var options = new Abstractions.Models.Configuration.BrowserOptions();
                    await controller.CloseAsync(options, allowAttached: false, cts.Token);
                }
                catch
                {
                    // Chrome cleanup must never make application shutdown fail.
                }
                finally
                {
                    completed.Set();
                }
            });

            completed.Wait(TimeSpan.FromSeconds(4));
        }
        catch
        {
            // Chrome cleanup must never make application shutdown fail or hang.
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
    {
        try
        {
            var line = $"{DateTime.UtcNow:O} {exception}{Environment.NewLine}{Environment.NewLine}";
            Directory.CreateDirectory(GetCrashLogDirectory(baseDirectory));
            File.AppendAllText(GetCrashLogPath(baseDirectory), line);
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
