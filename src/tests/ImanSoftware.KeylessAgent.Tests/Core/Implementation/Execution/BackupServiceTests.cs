using System.Diagnostics;
using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Execution;

/// <summary>
/// Backups and undo must never read from or write through links that leave the workspace.
/// mklink is used to plant real symlinks; environments without link rights skip silently.
/// </summary>
public class BackupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-backup-" + Guid.CreateVersion7().ToString("N"));
    private readonly BackupService _service =
        new(ImanFileStorageFactory.CreateStorage(), new FakeDateTimeProvider());

    public BackupServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task BackupAsync_RegularFile_IsCopiedIntoTheBatch()
    {
        var file = Path.Combine(_root, "data.txt");
        File.WriteAllText(file, "content");

        var outcome = await _service.BackupAsync(_root, file);

        outcome.IsSuccess.Should().BeTrue();
        Directory.EnumerateFiles(Path.Combine(_root, ".agent", "backups"), "*", SearchOption.AllDirectories)
            .Should().ContainSingle("the batch holds one file");
    }

    [Fact]
    public async Task BackupAsync_FileSymlinkPointingOutside_IsRejected()
    {
        var (link, outsideFile) = PlantFileSymlink("link.txt");
        if (link is null)
            return; // mklink unavailable in this environment (no symlink rights)

        try
        {
            var outcome = await _service.BackupAsync(_root, link!);

            outcome.IsFailure.Should().BeTrue();
            outcome.Error.Code.Should().Be(ErrorCodes.PathOutsideWorkspace);
        }
        finally
        {
            File.Delete(link!);
            TryDeleteOutside(outsideFile!);
        }
    }

    [Fact]
    public async Task UndoLatestAsync_TargetSwappedForSymlink_SkipsWithoutWritingOutside()
    {
        var file = Path.Combine(_root, "data.txt");
        File.WriteAllText(file, "original");
        (await _service.BackupAsync(_root, file)).IsSuccess.Should().BeTrue();

        // After the backup, the workspace path is swapped for a link pointing outside.
        var (link, outsideFile) = PlantFileSymlink("data.txt");
        if (link is null)
            return; // mklink unavailable in this environment (no symlink rights)

        try
        {
            link!.Should().Be(file);
            var outcome = await _service.UndoLatestAsync(_root);

            outcome.IsSuccess.Should().BeTrue("a skipped file must not abort the whole undo");
            File.ReadAllText(outsideFile!).Should().Be("outside",
                "undo must never write through a link that leaves the workspace");
            outcome.Data.Should().Contain(entry => entry.StartsWith("skipped:", StringComparison.Ordinal));
            outcome.Data.Should().NotContain("data.txt");
        }
        finally
        {
            File.Delete(file);
            TryDeleteOutside(outsideFile!);
        }
    }

    [Fact]
    public async Task UndoLatestAsync_RegularFile_RestoresContentAndClearsTheBatch()
    {
        var file = Path.Combine(_root, "data.txt");
        File.WriteAllText(file, "original");
        (await _service.BackupAsync(_root, file)).IsSuccess.Should().BeTrue();
        File.WriteAllText(file, "changed by the agent");

        var outcome = await _service.UndoLatestAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Should().Contain("data.txt");
        File.ReadAllText(file).Should().Be("original");
        Directory.EnumerateDirectories(Path.Combine(_root, ".agent", "backups"))
            .Should().BeEmpty("the restored batch is consumed");
    }

    /// <summary>
    /// Plants a file symlink named <paramref name="linkName"/> inside the workspace pointing to
    /// a file outside. Returns nulls when mklink is unavailable (developer mode / rights).
    /// </summary>
    private (string? Link, string? OutsideFile) PlantFileSymlink(string linkName)
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), "ka-bk-out-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsideFile = Path.Combine(outsideDir, "secret.txt");
        File.WriteAllText(outsideFile, "outside");

        var link = Path.Combine(_root, linkName);
        if (File.Exists(link))
            File.Delete(link);

        var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink \"{link}\" \"{outsideFile}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        mklink!.WaitForExit(5000);

        if (!File.Exists(link))
        {
            TryDeleteOutside(outsideFile);
            return (null, null); // mklink unavailable: Skip = "mklink unavailable"
        }

        return (link, outsideFile);
    }

    private static void TryDeleteOutside(string outsideFile)
    {
        try
        {
            File.Delete(outsideFile);
            Directory.Delete(Path.GetDirectoryName(outsideFile)!, recursive: true);
        }
        catch
        {
            // temp cleanup is best-effort
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup is best-effort */ }
    }
}
