using FluentAssertions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Snapshot;

public class SnapshotServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-snap-" + Guid.CreateVersion7().ToString("N"));
    private readonly IFileStorage _storage = ImanFileStorageFactory.CreateStorage();
    private readonly SnapshotService _service;

    public SnapshotServiceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class Program {}");
        // No bat files anywhere: the snapshots are generated natively by WorkspaceAnalyzer.
        _service = new SnapshotService(_storage, new WorkspaceAnalyzer(_storage));
    }

    [Fact]
    public async Task RefreshAsync_WorksWithoutAnyBatFiles()
    {
        var outcome = await _service.RefreshAsync(_root);

        outcome.IsSuccess.Should().BeTrue("the native analyzer needs no workspace scripts");
        _storage.FileExists(Path.Combine(_root, "tree-view-result.txt")).Should().BeTrue();
        _storage.FileExists(Path.Combine(_root, "code-dump-result.md")).Should().BeTrue();
    }

    [Fact]
    public async Task RefreshAsync_CodeDump_IncludesOnlyKnownExtensions()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "# readme");
        File.WriteAllBytes(Path.Combine(_root, "image.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "lib.dll"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(_root, "trace.log"), "log line");
        File.WriteAllBytes(Path.Combine(_root, "obj", "native.dll"), [7, 8, 9]);

        await _service.RefreshAsync(_root);

        var dump = await _storage.ReadAllTextAsync(Path.Combine(_root, "code-dump-result.md"));
        dump.IsSuccess.Should().BeTrue();
        dump.Data.Should().Contain("### Program.cs");
        dump.Data.Should().Contain("### README.md");
        dump.Data.Should().NotContain("### image.png", "binaries never belong in the dump");
        dump.Data.Should().NotContain("### lib.dll", "binaries never belong in the dump");
        dump.Data.Should().NotContain("### trace.log", "logs never belong in the dump");
        dump.Data.Should().NotContain("### obj", "excluded directories never enter the dump");
    }

    [Fact]
    public async Task RefreshAsync_CompletesQuickly()
    {
        var task = _service.RefreshAsync(_root);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));

        completed.Should().Be(task, "native generation must not hang");
        (await task).IsSuccess.Should().BeTrue();
        _storage.FileExists(Path.Combine(_root, "tree-view-result.txt")).Should().BeTrue();
        _storage.FileExists(Path.Combine(_root, "code-dump-result.md")).Should().BeTrue();
    }

    [Fact]
    public async Task BuildAttachmentsAsync_IncludesFilteredTreeAndDump()
    {
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, new KeylessOptions());

        outcome.IsSuccess.Should().BeTrue();
        var tree = outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.TreeView);
        tree.Content.Should().Contain("Program.cs");
        tree.Content.Should().NotContain("obj");
        var dump = outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.CodeDump);
        dump.Content.Should().Contain("### Program.cs");
        dump.Content.Should().NotContain("### code-dump-result");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_MissingOutputs_Fails()
    {
        var outcome = await _service.BuildAttachmentsAsync(_root, new KeylessOptions());

        outcome.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task IsStaleAsync_MissingOutputs_IsStale()
    {
        (await _service.IsStaleAsync(_root)).Data.Should().BeTrue();
    }

    [Fact]
    public async Task IsStaleAsync_FreshOutputs_IsNotStale()
    {
        await _service.RefreshAsync(_root);
        // Nothing was modified after the snapshot ran.
        await Task.Delay(1100);

        (await _service.IsStaleAsync(_root)).Data.Should().BeFalse();
    }

    [Fact]
    public async Task IsStaleAsync_NewerSource_IsStale()
    {
        await _service.RefreshAsync(_root);
        await Task.Delay(1100);
        File.WriteAllText(Path.Combine(_root, "NewSource.cs"), "class New {}");

        (await _service.IsStaleAsync(_root)).Data.Should().BeTrue();
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, System.IO.FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { /* best-effort */ }
    }
}
