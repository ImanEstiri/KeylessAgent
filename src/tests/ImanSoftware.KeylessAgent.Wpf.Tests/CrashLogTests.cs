using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests;

public sealed class CrashLogTests : IDisposable
{
    private readonly string _baseDirectory;

    public CrashLogTests()
    {
        _baseDirectory = Path.Combine(Path.GetTempPath(), "keyless-crash-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
            Directory.Delete(_baseDirectory, recursive: true);
    }

    [Fact]
    public void WriteCrashLog_CreatesTheDirectoryAndFile_WithATimestampAndTheException()
    {
        var failure = new InvalidOperationException("boom");

        App.WriteCrashLog(failure, _baseDirectory);

        var log = File.ReadAllText(App.GetCrashLogPath(_baseDirectory));
        log.Should().Contain("boom");
        log.Should().Contain(nameof(InvalidOperationException));
    }

    [Fact]
    public void WriteCrashLog_AppendsWithTimestamps()
    {
        App.WriteCrashLog(new Exception("first"), _baseDirectory);
        App.WriteCrashLog(new Exception("second"), _baseDirectory);

        var log = File.ReadAllText(App.GetCrashLogPath(_baseDirectory));
        log.Should().Contain("first");
        log.Should().Contain("second");
        log.IndexOf("first", StringComparison.Ordinal).Should().BeLessThan(log.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteCrashLog_NeverThrows_EvenWithAnImpossibleDirectory()
    {
        var impossible = Path.Combine(_baseDirectory, "file.txt", "cannot", "be", "a", "directory");

        var act = () => App.WriteCrashLog(new Exception("x"), impossible);

        act.Should().NotThrow();
    }

    [Fact]
    public void CrashLogPaths_FollowTheGuideContract()
    {
        App.GetCrashLogPath(_baseDirectory).Should().EndWith(Path.Combine("KeylessAgent", "wpf-crash.log"));
        App.GetCrashLogDirectory(_baseDirectory).Should().Be(Path.Combine(_baseDirectory, "KeylessAgent"));
    }
}
