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
        _service = new SnapshotService(_storage, new ImanSoftware.KeylessAgent.Core.Implementation.Execution.ProcessRunner());
        // Minimal stand-ins for the two workspace bat files; the real ones end with `pause`,
        // these end with a `pause` that also blocks, proving stdin-closing handles it.
        File.WriteAllText(Path.Combine(_root, "tree-view.bat"), "@echo off\r\ntree /F /A > tree-view-result.txt\r\n");
        File.WriteAllText(Path.Combine(_root, "code-dump.bat"), "@echo off\r\necho ### code-dump-result > code-dump-result.md\r\necho ### README >> code-dump-result.md\r\necho. >> code-dump-result.md\r\necho ```markdown >> code-dump-result.md\r\necho hi >> code-dump-result.md\r\necho ``` >> code-dump-result.md\r\npause\r\n");
    }

    [Fact]
    public async Task RefreshAsync_MissingBatFile_FailsWithSnapshotMissing()
    {
        File.Delete(Path.Combine(_root, "code-dump.bat"));

        var outcome = await _service.RefreshAsync(_root);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("KA_SNAPSHOT_MISSING");
    }

    [Fact]
    public async Task RefreshAsync_RunsBatsWithoutHangingOnPause()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var task = _service.RefreshAsync(_root);
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));

        completed.Should().Be(task, "the trailing pause must not hang the run (stdin is closed)");
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
        dump.Content.Should().Contain("### README");
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
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
