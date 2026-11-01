using System.IO;
using System.Windows;
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
