using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Configuration;

/// <summary>
/// The keyless.json schema migration: a pre-versioning file (no SchemaVersion key) is backed
/// up and upgraded to the current defaults with the common user customizations preserved;
/// a current file is left untouched.
/// </summary>
public class MigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-migration-" + Guid.CreateVersion7().ToString("N"));
    private readonly ConfigurationService _service = new();

    public MigrationTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string ConfigPath => Path.Combine(_root, "keyless.json");
    private string BackupPath => Path.Combine(_root, "keyless.json.v1.bak");

    [Fact]
    public async Task LoadAsync_StaleV1File_MigratesToCurrentDefaults_PreservingCustomizations()
    {
        File.WriteAllText(ConfigPath, """
            {
              "Mode": "Auto",
              "Browser": {
                "ReplyTimeoutSeconds": 300,
                "SendConfirmSeconds": 15,
                "UploadWaitSeconds": 60,
                "ChromeDebugPort": 9333
              }
            }
            """);
        var service = new ConfigurationService();

        var outcome = await service.LoadAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        service.LastLoadMigratedSchema.Should().BeTrue("a v1 file must be migrated");

        var migrated = outcome.Data;
        migrated.SchemaVersion.Should().Be(KeylessOptions.CurrentSchemaVersion);
        migrated.Browser.ReplyTimeoutSeconds.Should().Be(180, "the stale 300 is replaced with the tuned default");
        migrated.Browser.SendConfirmSeconds.Should().Be(8);
        migrated.Browser.UploadWaitSeconds.Should().Be(15);
        migrated.Mode.Should().Be(ApprovalMode.Auto, "the user's mode customization is preserved");
        migrated.Browser.ChromeDebugPort.Should().Be(9333, "the user's port customization is preserved");

        File.Exists(BackupPath).Should().BeTrue("the raw v1 file is backed up before the rewrite");
        var backup = File.ReadAllText(BackupPath);
        backup.Should().Contain("\"ReplyTimeoutSeconds\": 300");

        var onDisk = File.ReadAllText(ConfigPath);
        onDisk.Should().Contain("\"SchemaVersion\": 2");
        onDisk.Should().Contain("\"ReplyTimeoutSeconds\": 180");
        onDisk.Should().Contain("\"SendConfirmSeconds\": 8");
        onDisk.Should().Contain("\"UploadWaitSeconds\": 15");
        onDisk.Should().Contain("\"Mode\": \"Auto\"");
    }

    [Fact]
    public async Task LoadAsync_CurrentSchemaFile_IsLeftUntouched()
    {
        const string current = """
            {
              "SchemaVersion": 2,
              "Mode": "AutoSafe",
              "Browser": { "ReplyTimeoutSeconds": 42 }
            }
            """;
        File.WriteAllText(ConfigPath, current);
        var service = new ConfigurationService();

        var outcome = await service.LoadAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        service.LastLoadMigratedSchema.Should().BeFalse("a current file is never migrated");
        outcome.Data.Browser.ReplyTimeoutSeconds.Should().Be(42, "the user's value must survive");
        File.Exists(BackupPath).Should().BeFalse("no backup is written for a current file");
        File.ReadAllText(ConfigPath).Should().Contain("\"ReplyTimeoutSeconds\": 42", "the file is unchanged");
    }
}
