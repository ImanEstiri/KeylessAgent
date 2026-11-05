using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Configuration;

/// <summary>
/// The known-stale-default upgrades: a CURRENT-version keyless.json still carries old
/// default literals (CreateDefault serializes every field explicitly), so fields whose
/// shipped default changed later must be upgraded on EVERY load — but only when the value
/// exactly matches a known old default (anything else is a deliberate user customization).
/// </summary>
public class KnownStaleDefaultsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-stale-defaults-" + Guid.CreateVersion7().ToString("N"));
    private readonly CapturingLoggerFactory _loggerFactory = new();

    public KnownStaleDefaultsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string ConfigPath => Path.Combine(_root, "keyless.json");
    private string UpgradeBackupPath => Path.Combine(_root, "keyless.json.pre-defaults-upgrade.bak");
    private string SchemaBackupPath => Path.Combine(_root, "keyless.json.v1.bak");

    private ConfigurationService Service() => new(loggerFactory: _loggerFactory);

    [Fact]
    public async Task LoadAsync_MinimizedLiteral_AtCurrentSchemaVersion_IsUpgradedToTinyCorner()
    {
        File.WriteAllText(ConfigPath, """
            {
              "SchemaVersion": 2,
              "Browser": { "WindowMode": "Minimized" }
            }
            """);
        var service = Service();

        var outcome = await service.LoadAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Browser.WindowMode.Should().Be("TinyCorner",
            "the old shipped default 'Minimized' is upgraded even when SchemaVersion is current");
        service.LastLoadUpgradedFields.Should().ContainSingle()
            .Which.Should().Contain("Browser.WindowMode").And.Contain("Minimized").And.Contain("TinyCorner");

        File.Exists(UpgradeBackupPath).Should().BeTrue("the original file is backed up before the rewrite");
        File.ReadAllText(UpgradeBackupPath).Should().Contain("\"WindowMode\": \"Minimized\"");
        File.Exists(SchemaBackupPath).Should().BeFalse("no schema-version backup for a current file");

        var onDisk = File.ReadAllText(ConfigPath);
        onDisk.Should().Contain("\"WindowMode\": \"TinyCorner\"");
        onDisk.Should().Contain("\"SchemaVersion\": 2");

        _loggerFactory.Lines.Should().Contain(l =>
            l.Contains("Browser.WindowMode") && l.Contains("TinyCorner"),
            "the log line names the upgraded field");
    }

    [Fact]
    public async Task LoadAsync_OffscreenLiteral_IsUpgradedToo()
    {
        File.WriteAllText(ConfigPath, """
            {
              "SchemaVersion": 2,
              "Browser": { "WindowMode": "Offscreen" }
            }
            """);
        var service = Service();

        var outcome = await service.LoadAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Browser.WindowMode.Should().Be("TinyCorner");
        File.Exists(UpgradeBackupPath).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_UserCustomizedWindowMode_IsNeverTouched()
    {
        File.WriteAllText(ConfigPath, """
            {
              "SchemaVersion": 2,
              "Browser": { "WindowMode": "Normal" }
            }
            """);
        var service = Service();

        var outcome = await service.LoadAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Browser.WindowMode.Should().Be("Normal",
            "a value that matches no known old default is a deliberate user choice");
        service.LastLoadUpgradedFields.Should().BeEmpty();
        File.Exists(UpgradeBackupPath).Should().BeFalse("no rewrite, no backup");
        File.ReadAllText(ConfigPath).Should().Contain("\"WindowMode\": \"Normal\"", "the file is unchanged");
    }

    [Fact]
    public async Task LoadAsync_UpgradeIsIdempotent_SecondLoadChangesNothing()
    {
        File.WriteAllText(ConfigPath, """
            {
              "SchemaVersion": 2,
              "Browser": { "WindowMode": "Minimized" }
            }
            """);
        var service = Service();

        (await service.LoadAsync(_root)).IsSuccess.Should().BeTrue();
        File.Exists(UpgradeBackupPath).Should().BeTrue();

        var second = await service.LoadAsync(_root);

        second.Data.Browser.WindowMode.Should().Be("TinyCorner");
        service.LastLoadUpgradedFields.Should().BeEmpty("the file already carries the new default");
        File.ReadAllText(UpgradeBackupPath).Should().Contain("\"WindowMode\": \"Minimized\"",
            "the first load's backup is never overwritten or deleted");
    }

    [Fact]
    public async Task LoadAsync_UpgradeRewrite_PreservesOtherUserCustomizations()
    {
        File.WriteAllText(ConfigPath, """
            {
              "SchemaVersion": 2,
              "Browser": { "WindowMode": "Minimized", "ReplyTimeoutSeconds": 120 },
              "Mode": "Auto"
            }
            """);
        var service = Service();

        var outcome = await service.LoadAsync(_root);

        outcome.Data.Browser.WindowMode.Should().Be("TinyCorner");
        outcome.Data.Browser.ReplyTimeoutSeconds.Should().Be(120, "the user's value survives the rewrite");
        outcome.Data.Mode.Should().Be(ApprovalMode.Auto, "the user's mode survives the rewrite");
        File.ReadAllText(ConfigPath).Should().Contain("\"ReplyTimeoutSeconds\": 120");
    }

    /// <summary>ILoggerFactory that keeps every formatted log line for assertions.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Lines);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Add(formatter(state, exception));
        }
    }
}
