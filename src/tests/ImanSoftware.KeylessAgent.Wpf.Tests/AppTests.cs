using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests;

/// <summary>
/// The shutdown diagnostics: WriteCrashLog(string) appends plain lines to the crash log so
/// the OnExit Chrome-close safety net is debuggable from the log alone.
/// </summary>
public class AppTests : IDisposable
{
    private readonly string _baseDir = Path.Combine(Path.GetTempPath(), "ka-apptests-" + Guid.CreateVersion7().ToString("N"));

    public AppTests() => Directory.CreateDirectory(_baseDir);

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { }
    }

    [Fact]
    public void WriteCrashLog_StringOverload_AppendsALine()
    {
        App.WriteCrashLog("chrome-exit: confirmed — the debug port is closed", _baseDir);
        App.WriteCrashLog("chrome-exit: CloseAsync threw InvalidOperationException: boom", _baseDir);

        var lines = File.ReadAllLines(App.GetCrashLogPath(_baseDir));
        lines.Should().Contain(l => l.Contains("chrome-exit: confirmed"));
        lines.Should().Contain(l => l.Contains("InvalidOperationException") && l.Contains("boom"));
    }

    [Fact]
    public void WriteCrashLog_NeverThrows_WhenTheTargetDirectoryCannotBeCreated()
    {
        // A FILE where the log folder should be: CreateDirectory cannot proceed.
        var blockedBase = Path.Combine(_baseDir, "blocked", "log");
        File.WriteAllText(Path.Combine(_baseDir, "blocked"), "not a directory");

        var act = () => App.WriteCrashLog("some line", blockedBase);

        act.Should().NotThrow("crash/exit logging must never throw");
    }
}
