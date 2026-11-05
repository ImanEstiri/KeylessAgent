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

    /// <summary>Explicit Full-mode options: the tests below exercise the FULL dump path
    /// (masking, denylist, per-file cap), which is no longer the default mode.</summary>
    private static KeylessOptions FullOptions() => new()
    {
        Snapshot = new SnapshotOptions
        {
            Mode = SnapshotMode.Full,
            MaxTotalAttachmentChars = 400_000,
            MaxDumpedFileChars = 50_000,
        },
    };

    [Fact]
    public async Task BuildAttachmentsAsync_IncludesFilteredTreeAndDump()
    {
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, FullOptions());

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
        var outcome = await _service.BuildAttachmentsAsync(_root, FullOptions());

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

    [Fact]
    public async Task BuildAttachments_TreeAndChanged_ListsChangesSinceLastRefresh()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "two");
        await _service.RefreshAsync(_root);

        // The workspace moves on: one file modified, one added, one deleted.
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one-changed");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "three");
        File.Delete(Path.Combine(_root, "b.txt"));

        // Production order: the next turn's refresh stashes the diff (computed against the
        // PREVIOUS baseline) before overwriting it - then the attachments read the stash.
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(
            _root, new KeylessOptions { Snapshot = new SnapshotOptions { Mode = SnapshotMode.TreeAndChanged } });

        outcome.IsSuccess.Should().BeTrue();
        var changed = outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.ChangedFiles);
        changed.Name.Should().Be("changed-files.txt");
        changed.Content.Should().Contain("modified a.txt");
        changed.Content.Should().Contain("added c.txt");
        changed.Content.Should().Contain("deleted b.txt");
        outcome.Data.Should().Contain(a => a.Kind == SnapshotAttachmentKind.TreeView, "the tree is still attached");
        outcome.Data.Should().NotContain(a => a.Kind == SnapshotAttachmentKind.CodeDump,
            "TreeAndChanged replaces the dump with the changed-files list");
    }

    [Fact]
    public async Task BuildAttachments_TreeAndChanged_WithoutBaseline_FallsBackToFullDump()
    {
        await _service.RefreshAsync(_root);
        File.Delete(Path.Combine(_root, ".agent", "snapshot-baseline.json"));

        var outcome = await _service.BuildAttachmentsAsync(
            _root, new KeylessOptions { Snapshot = new SnapshotOptions { Mode = SnapshotMode.TreeAndChanged } });

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Should().Contain(a => a.Kind == SnapshotAttachmentKind.CodeDump,
            "without a baseline the full dump keeps the model operational");
        outcome.Data.Should().Contain(a => a.Kind == SnapshotAttachmentKind.CodeDump && a.Content.Contains("### Program.cs"));
        outcome.Data.Should().NotContain(a => a.Kind == SnapshotAttachmentKind.ChangedFiles);
        outcome.Data.Should().NotContain(a => a.Content.Contains("TreeAndChanged mode:"),
            "the placeholder must be gone");
    }

    [Fact]
    public async Task SensitiveFiles_FilteredFromAttachmentOnly()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET_TOKEN=abc123");
        File.WriteAllText(Path.Combine(_root, "secrets.json"), "{ \"ApiKey\": \"abc123\" }");
        File.WriteAllText(Path.Combine(_root, "appsettings.Production.json"), "{ \"Password\": \"prod-key\" }");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, FullOptions());

        outcome.IsSuccess.Should().BeTrue();
        var dump = outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.CodeDump);
        dump.Content.Should().NotContain("abc123", "secret values never reach the chat");
        dump.Content.Should().NotContain("prod-key");
        dump.Content.Should().NotContain("secrets.json", "sensitive files are dropped from the dump");
        dump.Content.Should().NotContain("appsettings.Production.json");
        outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.TreeView).Content
            .Should().Contain("[2 files filtered and 0 values masked as sensitive]");

        // The disk outputs are never modified.
        var onDisk = await _storage.ReadAllTextAsync(Path.Combine(_root, "code-dump-result.md"));
        onDisk.Data.Should().Contain("abc123").And.Contain("prod-key");
        var treeOnDisk = await _storage.ReadAllTextAsync(Path.Combine(_root, "tree-view-result.txt"));
        treeOnDisk.Data.Should().Contain(".env", "the tree on disk still lists everything");
    }

    [Fact]
    public async Task SecretValues_MaskedInAttachment()
    {
        var settings = string.Join("\n",
            "{",
            "  \"ApiKey\": \"abc123\",",
            "  \"Password\": \"hunter2\",",
            "  \"Retention\": 30",
            "}");
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), settings);
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, FullOptions());

        var dump = outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.CodeDump);
        dump.Content.Should().NotContain("abc123");
        dump.Content.Should().NotContain("hunter2");
        dump.Content.Should().Contain("\"ApiKey\": \"[redacted]\"", "the key name and JSON shape are kept");
        dump.Content.Should().Contain("\"Password\": \"[redacted]\"");
        dump.Content.Should().Contain("\"Retention\": 30", "non-secret values are untouched");
        outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.TreeView).Content
            .Should().Contain("[0 files filtered and 2 values masked as sensitive]");

        var onDisk = await _storage.ReadAllTextAsync(Path.Combine(_root, "code-dump-result.md"));
        onDisk.Data.Should().Contain("hunter2").And.Contain("abc123");
        (await _storage.ReadAllTextAsync(Path.Combine(_root, "appsettings.json"))).Data
            .Should().Contain("hunter2", "the source file on disk is never modified");
    }

    [Fact]
    public async Task CommentMentioningPassword_IsNotMasked()
    {
        File.WriteAllText(Path.Combine(_root, "normal.cs"),
            string.Join("\n", "// the password policy is documented elsewhere", "var retryCount = 1;"));
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, FullOptions());

        var dump = outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.CodeDump);
        dump.Content.Should().Contain("// the password policy is documented elsewhere",
            "a comment mentioning the word is not a secret");
        dump.Content.Should().Contain("var retryCount = 1;");
        outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.TreeView).Content
            .Should().NotContain("masked as sensitive", "nothing was filtered or masked");
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

    // --- Size-budget + mode-default behavior (turn attachment pipeline) ---

    [Fact]
    public void SnapshotOptions_Mode_DefaultsToTreeAndChanged()
    {
        new KeylessOptions().Snapshot.Mode.Should().Be(SnapshotMode.TreeAndChanged,
            "a fresh keyless.json must opt every turn into the diff attachment, not the full codebase");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_OverBudget_KeepsTreeAndReferencedFiles_OmitsTheRest()
    {
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), "class Alpha { }");
        File.WriteAllText(Path.Combine(_root, "Beta.cs"), "class Beta { /* referenced */ }");
        await _service.RefreshAsync(_root);

        var events = new List<(string Type, object Payload)>();
        var options = new KeylessOptions
        {
            Snapshot = new SnapshotOptions
            {
                Mode = SnapshotMode.Full,
                MaxTotalAttachmentChars = 250, // tiny ceiling: even this small workspace exceeds it
            },
        };

        var outcome = await _service.BuildAttachmentsAsync(
            _root, options, requestText: "fix Beta.cs please",
            logEvent: (type, payload) => { lock (events) events.Add((type, payload)); return Task.CompletedTask; });

        outcome.IsSuccess.Should().BeTrue();
        var tree = outcome.Data.First(a => a.Kind == SnapshotAttachmentKind.TreeView);
        var dump = outcome.Data.First(a => a.Kind == SnapshotAttachmentKind.CodeDump);

        tree.Content.Should().Contain("Alpha.cs").And.Contain("Beta.cs",
            "the tree is ALWAYS kept complete - it is cheap and essential for navigation");
        dump.Content.Should().Contain("class Beta", "the referenced file keeps its full content");
        dump.Content.Should().NotContain("class Alpha");
        dump.Content.Should().Contain("[omitted: 2 files under (workspace root)/ (", "Program.cs and Alpha.cs are both unreferenced")
            .And.Contain("file-read tool", "omitted files get a note pointing at the read tool");

        var totalAttached = outcome.Data.Sum(a => (long)a.Content.Length);
        totalAttached.Should().BeLessThan(1200,
            "the budget caps the TOTAL attached size, not just per-part chunking");

        lock (events)
        {
            var budgetEvents = events.Where(e => e.Type == "snapshot-budget-exceeded").ToList();
            budgetEvents.Should().HaveCount(1, "the turn log records the original total, the ceiling and the omissions");
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(budgetEvents[0].Payload);
            payload.GetProperty("OriginalTotalChars").GetInt32().Should().BeGreaterThan(250);
            payload.GetProperty("Ceiling").GetInt32().Should().Be(250);
            payload.GetProperty("OmittedFiles").GetInt32().Should().Be(2, "Alpha.cs AND the constructor-written Program.cs are unreferenced");
        }
    }

    [Fact]
    public async Task BuildAttachmentsAsync_SecondTurn_AfterRefresh_UsesTheChangedFilesDiff()
    {
        // Turn 1: refresh stashes no diff (no previous baseline) and captures the baseline -
        // the attachment takes the budget-limited dump path, never an unlimited full dump.
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), "class Alpha { }");
        await _service.RefreshAsync(_root);

        var first = await _service.BuildAttachmentsAsync(_root, new KeylessOptions());
        first.Data.Should().NotContain(a => a.Name == "changed-files.txt",
            "the first turn has no stashed diff and takes the budget-limited dump path");
        first.Data.First(a => a.Kind == SnapshotAttachmentKind.CodeDump)
            .Content.Should().Contain("class Alpha { }",
            "the first turn is NOT empty: the budget-limited dump still carries the code");

        // Turn 2: the agent created Beta.cs since the last refresh - the stashed diff
        // (computed BEFORE the baseline was overwritten) now shows exactly that change.
        File.WriteAllText(Path.Combine(_root, "Beta.cs"), "class Beta { }");
        await _service.RefreshAsync(_root);

        var second = await _service.BuildAttachmentsAsync(_root, new KeylessOptions());
        second.IsSuccess.Should().BeTrue();
        var changed = second.Data.FirstOrDefault(a => a.Name == "changed-files.txt");
        changed.Should().NotBeNull("the second turn diffs against the PREVIOUS refresh's baseline");
        changed!.Content.Should().Contain("Beta.cs", "only genuinely changed files are listed");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_OversizedFile_ShowsHeadingWithPlaceholder()
    {
        File.WriteAllText(Path.Combine(_root, "Huge.cs"), new string('x', 60_000));
        File.WriteAllText(Path.Combine(_root, "Small.cs"), "class Small { }");
        await _service.RefreshAsync(_root);

        var options = new KeylessOptions
        {
            Snapshot = new SnapshotOptions { Mode = SnapshotMode.Full, MaxTotalAttachmentChars = 400_000 },
        };
        var outcome = await _service.BuildAttachmentsAsync(_root, options);

        outcome.IsSuccess.Should().BeTrue();
        var dump = outcome.Data.First(a => a.Kind == SnapshotAttachmentKind.CodeDump);
        dump.Content.Should().Contain("### Huge.cs", "the heading keeps the file's existence visible");
        dump.Content.Should().Contain("[file too large to attach: ")
            .And.Contain("file-read tool");
        dump.Content.Should().NotContain(new string('x', 1000), "the full content never attaches");
        dump.Content.Should().Contain("class Small { }", "smaller files are unaffected");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_ExcludeFilePatterns_DropGeneratedAndVendorFiles()
    {
        File.WriteAllText(Path.Combine(_root, "package-lock.json"), "{ \"lockfileVersion\": 3 }");
        File.WriteAllText(Path.Combine(_root, "App.min.js"), "function a(){}");
        File.WriteAllText(Path.Combine(_root, "Keep.cs"), "class Keep { }");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, FullOptions());

        outcome.IsSuccess.Should().BeTrue();
        var dump = outcome.Data.First(a => a.Kind == SnapshotAttachmentKind.CodeDump);
        dump.Content.Should().NotContain("### package-lock.json", "lock files are on the denylist");
        dump.Content.Should().NotContain("### App.min.js", "minified bundles are on the denylist");
        dump.Content.Should().Contain("### Keep.cs");

        // The DISK copy stays untouched: filtering applies to the attached copy only.
        var onDisk = await _storage.ReadAllTextAsync(Path.Combine(_root, "code-dump-result.md"));
        onDisk.Data.Should().Contain("### package-lock.json");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_SmallWorkspace_UnderBudget_BehavesAsBefore()
    {
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class Program {}");
        File.WriteAllText(Path.Combine(_root, "README.md"), "# readme");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, FullOptions());

        outcome.IsSuccess.Should().BeTrue();
        var dump = outcome.Data.First(a => a.Kind == SnapshotAttachmentKind.CodeDump);
        dump.Content.Should().Contain("class Program {}").And.Contain("# readme",
            "a workspace that fits the budget attaches everything, unchanged");
        dump.Content.Should().NotContain("[omitted:", "no omission notes for a small workspace");
    }
}
