using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;

/// <summary>
/// Produces the workspace snapshots natively in C# (no .bat files required) and turns
/// them into filtered, chunked attachments.
/// </summary>
internal sealed class SnapshotService : ISnapshotService
{
    private const string TreeResultName = "tree-view-result.txt";
    private const string CodeDumpResultName = "code-dump-result.md";
    private const int MaxAttachmentChars = 240_000;
    private static readonly string BaselineRelativePath = Path.Combine(".agent", "snapshot-baseline.json");

    private readonly IFileStorage _storage;
    private readonly WorkspaceAnalyzer _analyzer;
    private readonly IChangeTracker _changeTracker;

    public SnapshotService()
        : this(ImanFileStorageFactory.CreateStorage(), new WorkspaceAnalyzer(ImanFileStorageFactory.CreateStorage()))
    {
    }

    internal SnapshotService(IFileStorage storage, WorkspaceAnalyzer analyzer, IChangeTracker? changeTracker = null)
    {
        _storage = storage;
        _analyzer = analyzer;
        _changeTracker = changeTracker ?? new ChangeTracker();
    }

    /// <inheritdoc />
    public async Task<Outcome> RefreshAsync(string workspaceRoot)
    {
        // The snapshots are generated natively in C# (WorkspaceAnalyzer): no .bat files needed.
        try
        {
            await _analyzer.WriteTreeViewAsync(workspaceRoot);
            await _analyzer.WriteCodeDumpAsync(workspaceRoot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Outcome.Failure(new OutcomeError(
                $"Snapshot generation failed: {ex.Message}",
                ErrorCodes.SnapshotRunFailed, OutcomeErrorType.Failure));
        }

        foreach (var result in (string[])[TreeResultName, CodeDumpResultName])
        {
            if (!_storage.FileExists(Path.Combine(workspaceRoot, result)))
            {
                return Outcome.Failure(new OutcomeError(
                    $"Snapshot output was not produced: {result}.",
                    ErrorCodes.SnapshotRunFailed, OutcomeErrorType.Failure));
            }
        }

        // The baseline powers TreeAndChanged mode; a failed capture only means the next
        // BuildAttachments falls back to the full dump, so it must never fail the refresh.
        await UpdateSnapshotBaselineAsync(workspaceRoot);

        return Outcome.Successful();
    }

    /// <summary>
    /// Captures the workspace state after a successful refresh and persists it under
    /// <c>.agent/snapshot-baseline.json</c>: git porcelain when git answers, a path/size/mtime
    /// index otherwise. Best-effort — baseline problems degrade to the full dump, never fail.
    /// </summary>
    private async Task UpdateSnapshotBaselineAsync(string workspaceRoot)
    {
        // git status "succeeds" with empty stdout even OUTSIDE a repository, which would make
        // every later diff vacuously empty — probe the workspace for real before trusting it.
        var repoProbe = await new ProcessRunner().RunAsync("git", "rev-parse --is-inside-work-tree", workspaceRoot, 15);
        var gitAvailable = repoProbe.ExitCode == 0;

        bool porcelainUsed;
        List<string>? porcelain = null;
        Dictionary<string, (long Size, DateTime Mtime)>? index = null;

        var capture = await _changeTracker.CaptureAsync(workspaceRoot, gitAvailable);
        if (gitAvailable && capture.IsSuccess && capture.Data is List<string> statusLines)
        {
            porcelainUsed = true;
            porcelain = statusLines;
        }
        else
        {
            if (!gitAvailable)
                capture = await _changeTracker.CaptureAsync(workspaceRoot, false);
            if (capture.IsFailure ||
                capture.Data is not Dictionary<string, (long Size, DateTime Mtime)> capturedIndex)
                return;
            porcelainUsed = false;
            index = capturedIndex;
        }

        var baseline = new BaselineFile(
            porcelainUsed,
            porcelain,
            index?.ToDictionary(
                kv => kv.Key,
                kv => new[] { kv.Value.Size, kv.Value.Mtime.ToFileTimeUtc() },
                StringComparer.OrdinalIgnoreCase));

        var baselinePath = Path.Combine(workspaceRoot, BaselineRelativePath);
        var mkdir = await _storage.CreateDirectoryAsync(Path.GetDirectoryName(baselinePath)!);
        if (mkdir.IsFailure)
            return;

        await _storage.WriteAllTextAsync(baselinePath, System.Text.Json.JsonSerializer.Serialize(baseline));
    }

    /// <inheritdoc />
    public async Task<Outcome<IReadOnlyList<SnapshotAttachment>>> BuildAttachmentsAsync(string workspaceRoot, KeylessOptions options)
    {
        var treePath = Path.Combine(workspaceRoot, TreeResultName);
        var dumpPath = Path.Combine(workspaceRoot, CodeDumpResultName);

        if (!_storage.FileExists(treePath) || !_storage.FileExists(dumpPath))
        {
            return Outcome.Failure<IReadOnlyList<SnapshotAttachment>>(new OutcomeError(
                "Snapshot outputs are missing; run a refresh first.",
                ErrorCodes.SnapshotMissing, OutcomeErrorType.NotFound));
        }

        var treeText = await _storage.ReadAllTextAsync(treePath);
        if (treeText.IsFailure)
            return Outcome.Failure<IReadOnlyList<SnapshotAttachment>>(treeText.Error);

        var dumpText = await _storage.ReadAllTextAsync(dumpPath);
        if (dumpText.IsFailure)
            return Outcome.Failure<IReadOnlyList<SnapshotAttachment>>(dumpText.Error);

        var filter = options.Snapshot.Filter;
        var tree = string.Join("\n", SnapshotFilter.FilterTreeLines(
            treeText.Data.Replace("\r\n", "\n").Split('\n'), filter.ExcludeTreePatterns)) + "\n";

        var attachments = new List<SnapshotAttachment>
        {
            new(TreeResultName, SnapshotAttachmentKind.TreeView, tree),
        };

        // TreeAndChanged: the tree plus what changed since the last refresh — the full dump is
        // replaced; anything unchanged is one readfile away. Without a usable baseline (first
        // run, unreadable file, failed diff) the full dump keeps the model operational.
        if (options.Snapshot.Mode == SnapshotMode.TreeAndChanged)
        {
            var changedFiles = await BuildChangedFilesContentAsync(workspaceRoot);
            if (changedFiles is not null)
            {
                attachments.Add(new SnapshotAttachment("changed-files.txt", SnapshotAttachmentKind.ChangedFiles, changedFiles));
                return Outcome.Successful((IReadOnlyList<SnapshotAttachment>)attachments);
            }
        }

        var dump = SnapshotFilter.FilterDumpSections(dumpText.Data, filter.ExcludeDumpHeadings);
        var parts = SnapshotFilter.ChunkDump(dump, MaxAttachmentChars);
        for (var i = 0; i < parts.Count; i++)
        {
            var name = parts.Count == 1 ? CodeDumpResultName : $"code-dump-result-part{i + 1}.md";
            attachments.Add(new SnapshotAttachment(name, SnapshotAttachmentKind.CodeDump, parts[i]));
        }

        return Outcome.Successful((IReadOnlyList<SnapshotAttachment>)attachments);
    }

    /// <summary>
    /// Diffs the workspace against the persisted baseline and renders "<kind> <path>" lines;
    /// null when there is no usable baseline or the diff failed (fall back to the full dump).
    /// </summary>
    private async Task<string?> BuildChangedFilesContentAsync(string workspaceRoot)
    {
        var baselinePath = Path.Combine(workspaceRoot, BaselineRelativePath);
        if (!_storage.FileExists(baselinePath))
            return null;

        var read = await _storage.ReadAllTextAsync(baselinePath);
        if (read.IsFailure)
            return null;

        BaselineFile? baseline;
        try
        {
            baseline = System.Text.Json.JsonSerializer.Deserialize<BaselineFile>(read.Data);
        }
        catch
        {
            return null;
        }

        if (baseline is null)
            return null;

        object handle = baseline.GitAvailable
            ? (object?)baseline.Porcelain ?? new List<string>()
            : (baseline.Index ?? []).ToDictionary(
                kv => kv.Key,
                kv => (kv.Value[0], DateTime.FromFileTimeUtc(kv.Value[1])),
                StringComparer.OrdinalIgnoreCase);

        var diff = await _changeTracker.DiffAsync(workspaceRoot, handle, baseline.GitAvailable);
        if (diff.IsFailure || diff.Data is null)
            return null;

        var lines = diff.Data.Select(c => $"{c.Kind} {c.Path}").ToList();
        return (lines.Count > 0 ? string.Join("\n", lines) : "(no changes since the last snapshot.)") + "\n";
    }

    private sealed record BaselineFile(
        bool GitAvailable,
        List<string>? Porcelain,
        Dictionary<string, long[]>? Index);

    /// <inheritdoc />
    public async Task<Outcome<bool>> IsStaleAsync(string workspaceRoot)
    {
        var treePath = Path.Combine(workspaceRoot, TreeResultName);
        var dumpPath = Path.Combine(workspaceRoot, CodeDumpResultName);

        if (!_storage.FileExists(treePath) || !_storage.FileExists(dumpPath))
            return Outcome.Successful(true);

        var treeStamp = (await _storage.GetMetadataAsync(treePath)).Data?.LastModifiedAt;
        var dumpStamp = (await _storage.GetMetadataAsync(dumpPath)).Data?.LastModifiedAt;
        var oldest = new[] { treeStamp, dumpStamp }.Where(t => t.HasValue).Select(t => t!.Value).DefaultIfEmpty().Min();

        var sources = await _storage.SearchFilesAsync(
            workspaceRoot, ["*.cs", "*.csproj", "*.slnx", "*.json", "*.md"], SearchOption.AllDirectories);
        if (sources.IsFailure)
            return Outcome.Successful(false);

        var newestSource = sources.Data?
            .Where(f => !IsUnderExcludedFolder(workspaceRoot, f.FullPath) &&
                        !IsSnapshotOutput(f.FileName))
            .Select(f => f.LastModifiedAt)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .DefaultIfEmpty()
            .Max();

        return Outcome.Successful(newestSource > oldest);
    }

    private static bool IsSnapshotOutput(string fileName)
        => fileName is TreeResultName or CodeDumpResultName;

    private static bool IsUnderExcludedFolder(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj" or ".git" or ".agent" or "node_modules" or "artifacts");
    }
}
