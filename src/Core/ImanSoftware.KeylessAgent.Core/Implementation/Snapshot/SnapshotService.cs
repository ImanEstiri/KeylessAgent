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
/// Runs the two workspace bat files and produces filtered, chunked attachments.
/// Bat files run through <c>cmd /c</c> without a window, stdin closed, hard timeout —
/// the final <c>pause</c> can never hang the agent.
/// </summary>
internal sealed class SnapshotService : ISnapshotService
{
    private const string TreeBatName = "tree-view.bat";
    private const string CodeDumpBatName = "code-dump.bat";
    private const string TreeResultName = "tree-view-result.txt";
    private const string CodeDumpResultName = "code-dump-result.md";
    private const int MaxAttachmentChars = 240_000;

    private readonly IFileStorage _storage;
    private readonly ProcessRunner _runner;

    public SnapshotService()
        : this(ImanFileStorageFactory.CreateStorage(), new ProcessRunner())
    {
    }

    internal SnapshotService(IFileStorage storage, ProcessRunner runner)
    {
        _storage = storage;
        _runner = runner;
    }

    /// <inheritdoc />
    public async Task<Outcome> RefreshAsync(string workspaceRoot)
    {
        foreach (var bat in (string[])[TreeBatName, CodeDumpBatName])
        {
            var batPath = Path.Combine(workspaceRoot, bat);
            if (!_storage.FileExists(batPath))
            {
                return Outcome.Failure(new OutcomeError(
                    $"Workspace snapshot script not found: {bat}. It must sit in the workspace root.",
                    ErrorCodes.SnapshotMissing, OutcomeErrorType.NotFound));
            }
        }

        var tree = await _runner.RunAsync("cmd.exe", $"/c \"{TreeBatName}\"", workspaceRoot, timeoutSeconds: 120);
        var dump = await _runner.RunAsync("cmd.exe", $"/c \"{CodeDumpBatName}\"", workspaceRoot, timeoutSeconds: 600);

        if (tree.TimedOut || dump.TimedOut)
        {
            return Outcome.Failure(new OutcomeError(
                "A snapshot bat file timed out and was terminated.",
                ErrorCodes.Timeout, OutcomeErrorType.Failure));
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
