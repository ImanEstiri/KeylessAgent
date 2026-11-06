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
    private static KeylessOptions Options(
        ContextMode? first = null, ContextMode? def = null,
        int? maxTotal = null, int? maxFile = null, int? maxPaths = null) => new()
    {
        Snapshot = new SnapshotOptions
        {
            FirstMessageMode = first ?? ContextMode.Snapshot,
            DefaultMode = def ?? ContextMode.Changed,
            MaxTotalAttachmentChars = maxTotal ?? 400_000,
            MaxDumpedFileChars = maxFile ?? 50_000,
            MaxContextFiles = maxPaths ?? 20,
        },
    };

    private const string WorkspaceContextName = "workspace-context.md";

    [Fact]
    public async Task BuildAttachmentsAsync_SnapshotMode_ReturnsExactlyOneWorkspaceContextAttachment()
    {
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), "class Alpha { }");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Snapshot);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Should().ContainSingle("ONE attachment per message, every mode");
        var context = outcome.Data[0];
        context.Name.Should().Be(WorkspaceContextName);
        context.Kind.Should().Be(SnapshotAttachmentKind.WorkspaceContext);
        context.Content.Should().StartWith("[CONTEXT mode=snapshot ");
        context.Content.Should().Contain("## tree").And.Contain("## files");
        context.Content.Should().Contain("### Alpha.cs").And.Contain("class Alpha { }");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_SnapshotMode_ListsNonTextFilesAsPathOnly()
    {
        File.WriteAllBytes(Path.Combine(_root, "logo.png"), [1, 2, 3]);
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Snapshot);

        outcome.IsSuccess.Should().BeTrue();
        var context = outcome.Data[0];
        context.Content.Should().Contain("[non-text files (path only)]");
        context.Content.Should().Contain("logo.png");
        context.Content.Should().NotContain("### logo.png", "non-text files never carry content");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_ChangedMode_NoBaseline_AttachesNothing()
    {
        await _service.RefreshAsync(_root);
        File.Delete(Path.Combine(_root, ".agent", "snapshot-baseline.json"));

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Changed);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Should().BeEmpty("a changed build without a baseline cannot diff");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_ChangedMode_SecondBuild_ShowsOnlyChanges_AndRefreshesBaseline()
    {
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), "class Alpha { }");
        await _service.RefreshAsync(_root); // initial baseline capture

        // The agent creates a file; the changed build must show it with FULL content.
        File.WriteAllText(Path.Combine(_root, "Beta.cs"), "class Beta { }");
        var first = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Changed);

        first.IsSuccess.Should().BeTrue();
        first.Data.Should().ContainSingle();
        var context = first.Data[0];
        context.Content.Should().Contain("[CONTEXT mode=changed ");
        context.Content.Should().Contain("added Beta.cs", "the change list carries the status");
        context.Content.Should().Contain("### Beta.cs").And.Contain("class Beta { }",
            "changed text files attach their FULL current content read from disk");
        context.Content.Should().NotContain("## tree", "the changed mode carries a tree DELTA, not the whole tree");

        // Refreshed after use: a second build right after sees NO changes.
        var second = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Changed);
        second.IsSuccess.Should().BeTrue();
        second.Data.Should().BeEmpty("nothing changed since the previous context build");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_FilesMode_AttachesExplicitPaths_WithinTheSandbox()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Keep.cs"), "class Keep { }");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(
            _root, Options(), ContextMode.Files, explicitPaths: ["src/Keep.cs", "../../outside.txt", "src/Missing.cs"]);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Should().ContainSingle();
        var context = outcome.Data[0];
        context.Content.Should().Contain("[CONTEXT mode=files ");
        context.Content.Should().Contain("### src/Keep.cs").And.Contain("class Keep { }");
        context.Content.Should().Contain("[skipped:", "paths outside the workspace are sandboxed away");
        context.Content.Should().Contain("[unreadable]");
        context.Content.Should().NotContain("../../outside", "the sandbox never resolves escapes");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_FilesMode_CapsThePathCount()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(_root, "src", $"F{i}.cs"), $"class F{i} {{ }}");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(
            _root, Options(maxPaths: 2), ContextMode.Files,
            explicitPaths: ["src/F0.cs", "src/F1.cs", "src/F2.cs", "src/F3.cs", "src/F4.cs"]);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data[0].Content.Should().Contain("### src/F0.cs").And.Contain("### src/F1.cs");
        outcome.Data[0].Content.Should().NotContain("### src/F2.cs", "only MaxContextFiles paths attach");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_NoneMode_AttachesNothing()
    {
        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Should().BeEmpty();
    }

    [Fact]
    public void SnapshotOptions_FirstMessageMode_DefaultsToSnapshot_DefaultModeToChanged()
    {
        var options = new SnapshotOptions();
        options.FirstMessageMode.Should().Be(ContextMode.Snapshot);
        options.DefaultMode.Should().Be(ContextMode.Changed);
        options.MaxContextFiles.Should().Be(20);
    }

    [Fact]
    public async Task BuildAttachmentsAsync_SensitiveFiles_NeverAttached_InAnyMode()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET_TOKEN=abc123");
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{ \"ApiKey\": \"abc123\" }");
        await _service.RefreshAsync(_root);

        var snapshot = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Snapshot);
        snapshot.Data[0].Content.Should().NotContain("### .env").And.NotContain("SECRET_TOKEN=abc123");
        snapshot.Data[0].Content.Should().Contain("## tree", "the tree is part of the single attachment");
        snapshot.Data[0].Content.Should().Contain("\"ApiKey\": \"[redacted]\"");

        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{ \"ApiKey\": \"abc123\" }");
        await _service.RefreshAsync(_root);
        var changed = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Changed);
        if (changed.Data.Count > 0)
            changed.Data[0].Content.Should().NotContain("SECRET_TOKEN=abc123");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_OversizedFile_ShowsHeadingWithPlaceholder()
    {
        File.WriteAllText(Path.Combine(_root, "Huge.cs"), new string('x', 60_000));
        File.WriteAllText(Path.Combine(_root, "Small.cs"), "class Small { }");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Snapshot);

        outcome.IsSuccess.Should().BeTrue();
        var context = outcome.Data[0];
        context.Content.Should().Contain("### Huge.cs", "the heading keeps the file's existence visible");
        context.Content.Should().Contain("[file too large to attach: ");
        context.Content.Should().NotContain(new string('x', 1000), "the full content never attaches");
        context.Content.Should().Contain("class Small { }", "smaller files are unaffected");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_ExcludeFilePatterns_DropGeneratedAndVendorFiles()
    {
        File.WriteAllText(Path.Combine(_root, "package-lock.json"), "{ \"lockfileVersion\": 3 }");
        File.WriteAllText(Path.Combine(_root, "App.min.js"), "function a(){}");
        File.WriteAllText(Path.Combine(_root, "Keep.cs"), "class Keep { }");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Snapshot);

        outcome.IsSuccess.Should().BeTrue();
        var context = outcome.Data[0];
        context.Content.Should().NotContain("### package-lock.json", "lock files are on the denylist");
        context.Content.Should().NotContain("### App.min.js", "minified bundles are on the denylist");
        context.Content.Should().Contain("### Keep.cs");

        var onDisk = await _storage.ReadAllTextAsync(Path.Combine(_root, "code-dump-result.md"));
        onDisk.Data.Should().Contain("### package-lock.json", "disk copies are never touched");
    }

    [Fact]
    public async Task BuildAttachmentsAsync_OverBudget_KeepsReferencedFiles_OmitsTheRest_InOneAttachment()
    {
        File.WriteAllText(Path.Combine(_root, "Alpha.cs"), "class Alpha { }");
        File.WriteAllText(Path.Combine(_root, "Beta.cs"), "class Beta { /* referenced */ }");
        await _service.RefreshAsync(_root);

        var events = new List<(string Type, object Payload)>();
        var outcome = await _service.BuildAttachmentsAsync(
            _root, Options(maxTotal: 300), ContextMode.Snapshot, requestText: "fix Beta.cs please",
            logEvent: (type, payload) => { lock (events) events.Add((type, payload)); return Task.CompletedTask; });

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Should().ContainSingle("no multi-part chunking: exactly ONE attachment");
        var context = outcome.Data[0];
        context.Content.Should().Contain("## tree", "the tree always survives the budget");
        context.Content.Should().Contain("class Beta { /* referenced */ }", "request-referenced files keep full content");
        context.Content.Should().NotContain("class Alpha { }");
        context.Content.Should().Contain("[omitted:", "omitted files get per-folder notes");
        context.Content.Length.Should().BeLessThan(1500, "the single attachment stays inside the ceiling");

        lock (events)
        {
            var budgetEvents = events.Where(e => e.Type == "context-budget-exceeded").ToList();
            budgetEvents.Should().HaveCount(1);
            var payload = System.Text.Json.JsonSerializer.SerializeToElement(budgetEvents[0].Payload);
            payload.GetProperty("Ceiling").GetInt32().Should().Be(300);
            payload.GetProperty("OmittedFiles").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        }
    }

    [Fact]
    public async Task BuildAttachmentsAsync_SmallWorkspace_UnderBudget_BehavesAsBefore()
    {
        File.WriteAllText(Path.Combine(_root, "Program.cs"), "class Program {}");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Snapshot);

        outcome.Data[0].Content.Should().Contain("class Program {}");
        outcome.Data[0].Content.Should().NotContain("[omitted:", "no omission notes for a small workspace");
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
    public async Task BuildAttachments_ChangedMode_ListsChangesSinceLastBuild()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "two");
        await _service.RefreshAsync(_root); // initial baseline

        // The workspace moves on: one file modified, one added, one deleted.
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one-changed");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "three");
        File.Delete(Path.Combine(_root, "b.txt"));

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Changed);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Should().ContainSingle();
        var context = outcome.Data[0];
        context.Name.Should().Be(WorkspaceContextName);
        context.Kind.Should().Be(SnapshotAttachmentKind.WorkspaceContext);
        context.Content.Should().Contain("modified a.txt");
        context.Content.Should().Contain("added c.txt");
        context.Content.Should().Contain("deleted b.txt");
        context.Content.Should().Contain("### c.txt").And.Contain("three",
            "added text files carry their full current content");
        context.Content.Should().Contain("### a.txt").And.Contain("one-changed");
        context.Content.Should().Contain("[deleted since the last context build]");
    }

    [Fact]
    public async Task BuildAttachments_ChangedMode_BaselineRefreshesAfterUse_CoversTheNextChanges()
    {
        // Turn 1: refresh captures the initial baseline; the changed build shows turn-1
        // changes (created files) and refreshes the baseline after use.
        File.WriteAllText(Path.Combine(_root, "a.txt"), "one");
        await _service.RefreshAsync(_root);
        File.WriteAllText(Path.Combine(_root, "c.txt"), "three");

        var first = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Changed);
        first.Data[0].Content.Should().Contain("added c.txt");

        // Turn 2: nothing changed since the previous BUILD - the next build attaches nothing.
        var second = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Changed);
        second.Data.Should().BeEmpty("the baseline was refreshed after the previous use");
    }

    [Fact]
    public async Task SensitiveFiles_FilteredFromAttachmentOnly()
    {
        File.WriteAllText(Path.Combine(_root, ".env"), "SECRET_TOKEN=abc123");
        File.WriteAllText(Path.Combine(_root, "secrets.json"), "{ \"ApiKey\": \"abc123\" }");
        File.WriteAllText(Path.Combine(_root, "appsettings.Production.json"), "{ \"Password\": \"prod-key\" }");
        await _service.RefreshAsync(_root);

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Snapshot);

        outcome.IsSuccess.Should().BeTrue();
        var dump = outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.WorkspaceContext);
        dump.Content.Should().NotContain("abc123", "secret values never reach the chat");
        dump.Content.Should().NotContain("prod-key");
        dump.Content.Should().NotContain("### secrets.json", "sensitive sections are dropped from the files part");
        dump.Content.Should().NotContain("### appsettings.Production.json");
        outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.WorkspaceContext).Content
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

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Snapshot);

        var dump = outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.WorkspaceContext);
        dump.Content.Should().NotContain("abc123");
        dump.Content.Should().NotContain("hunter2");
        dump.Content.Should().Contain("\"ApiKey\": \"[redacted]\"", "the key name and JSON shape are kept");
        dump.Content.Should().Contain("\"Password\": \"[redacted]\"");
        dump.Content.Should().Contain("\"Retention\": 30", "non-secret values are untouched");
        outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.WorkspaceContext).Content
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

        var outcome = await _service.BuildAttachmentsAsync(_root, Options(), ContextMode.Snapshot);

        var dump = outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.WorkspaceContext);
        dump.Content.Should().Contain("// the password policy is documented elsewhere",
            "a comment mentioning the word is not a secret");
        dump.Content.Should().Contain("var retryCount = 1;");
        outcome.Data.Single(a => a.Kind == SnapshotAttachmentKind.WorkspaceContext).Content
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




}
