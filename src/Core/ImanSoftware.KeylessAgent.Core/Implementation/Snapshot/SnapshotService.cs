using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Abstractions.Models.Snapshot;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
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

    private readonly IFileStorage _storage;
    private readonly WorkspaceAnalyzer _analyzer;

    public SnapshotService()
        : this(ImanFileStorageFactory.CreateStorage(), new WorkspaceAnalyzer(ImanFileStorageFactory.CreateStorage()))
    {
    }

    internal SnapshotService(IFileStorage storage, WorkspaceAnalyzer analyzer)
    {
        _storage = storage;
        _analyzer = analyzer;
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

        return Outcome.Successful();
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

        var dump = SnapshotFilter.FilterDumpSections(dumpText.Data, filter.ExcludeDumpHeadings);
        if (options.Snapshot.Mode == SnapshotMode.TreeAndChanged && dump.Length > 0)
            dump = "(TreeAndChanged mode: attach the tree plus ask for files with readfile.)\n";

        var parts = SnapshotFilter.ChunkDump(dump, MaxAttachmentChars);
        for (var i = 0; i < parts.Count; i++)
        {
            var name = parts.Count == 1 ? CodeDumpResultName : $"code-dump-result-part{i + 1}.md";
            attachments.Add(new SnapshotAttachment(name, SnapshotAttachmentKind.CodeDump, parts[i]));
        }

        return Outcome.Successful((IReadOnlyList<SnapshotAttachment>)attachments);
    }

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
