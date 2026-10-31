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

        // Sensitive content never reaches the chat: secret files are dropped from the attached
        // dump and secret-looking values in the remaining sections are redacted — the copies
        // only; code-dump-result.md and tree-view-result.txt on disk stay untouched.
        var dump = SnapshotFilter.FilterDumpSections(dumpText.Data, filter.ExcludeDumpHeadings);
        var (sanitizedDump, filesDropped, valuesMasked) =
            SanitizeSensitiveContent(dump, filter.SensitiveFilePatterns, filter.MaskSensitiveLines);
        if (filesDropped + valuesMasked > 0)
            tree = $"[{filesDropped} files filtered and {valuesMasked} values masked as sensitive]\n" + tree;

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

        var parts = SnapshotFilter.ChunkDump(sanitizedDump, MaxAttachmentChars);
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

    // --- Sensitive-content sanitation (attachment copy only; disk outputs stay untouched) ---

    /// <summary>
    /// Drops dump sections whose file name matches a sensitive pattern and redacts
    /// secret-looking values in the remaining sections. Returns the sanitized dump plus the
    /// counters for the tree marker line.
    /// </summary>
    internal static (string Dump, int FilesDropped, int ValuesMasked) SanitizeSensitiveContent(
        string dump, IReadOnlyList<string> sensitivePatterns, bool maskValues)
    {
        var filesDropped = 0;
        var valuesMasked = 0;
        var result = new System.Text.StringBuilder();

        // Sections are delimited by "### heading" lines — the same convention
        // SnapshotFilter.FilterDumpSections uses.
        var lines = dump.Replace("\r\n", "\n").Split('\n');
        var skipping = false;
        var inPem = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var heading = line["### ".Length..].Trim();
                var fileName = HeadingFileName(heading);
                skipping = MatchesSensitivePattern(fileName, sensitivePatterns);
                if (skipping)
                {
                    filesDropped++;
                    continue;
                }
            }

            if (skipping)
                continue;

            if (maskValues)
            {
                // PEM private-key blocks are redacted line-wise between BEGIN and END.
                if (inPem)
                {
                    result.AppendLine("[redacted]");
                    if (PemEndRegex.IsMatch(line))
                        inPem = false;
                    continue;
                }

                var (masked, count) = MaskSecretLine(line);
                valuesMasked += count;
                if (PemBeginRegex.IsMatch(masked))
                    inPem = true;
                result.AppendLine(masked);
            }
            else
            {
                result.AppendLine(line);
            }
        }

        return (result.ToString(), filesDropped, valuesMasked);
    }

    /// <summary>The file name a dump heading refers to (headings use " &gt; " separators).</summary>
    internal static string HeadingFileName(string heading)
    {
        var lastSegment = heading
            .Split(" > ", StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?.Trim() ?? heading;
        var slash = lastSegment.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? lastSegment[(slash + 1)..] : lastSegment;
    }

    /// <summary>Case-insensitive <c>*</c> wildcard match against the file name.</summary>
    internal static bool MatchesSensitivePattern(string fileName, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.Length == 0)
                continue;

            var regex = "^" + System.Text.RegularExpressions.Regex
                .Escape(pattern)
                .Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch(fileName, regex,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                return true;
        }

        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex AssignmentSecretQuotedRegex = new(
        "(\\b(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|credential|connection[_-]?string)s?\\b\\\"?\\s*[=:]\\s*)(\\\"[^\\\"]*\\\")",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex AssignmentSecretRegex = new(
        "(\\b(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|credential|connection[_-]?string)s?\\b\\\"?\\s*[=:]\\s*)(\\\"[^\\\"]*\\\"|\\S+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex AuthorizationRegex = new(
        "((?:authorization|auth)\\b\\s*[=:]\\s*\"?(?:bearer|basic)\\s+)[^\"]+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex PemBeginRegex = new(
        "-----BEGIN [A-Z ]*PRIVATE KEY-----",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex PemEndRegex = new(
        "-----END [A-Z ]*PRIVATE KEY-----",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Redacts the value part of one secret-looking line, keeping the key name and structure:
    /// assignments and JSON keys become key=[redacted] (quotes dropped on assignments,
    /// JSON keeps the shape), Authorization/Bearer tokens are redacted wholesale. PEM blocks
    /// are handled line-wise by the caller (begin/end state). Returns the (possibly
    /// unchanged) line and how many values were masked.
    /// </summary>
    internal static (string Line, int MaskedCount) MaskSecretLine(string line)
    {
        if (PemBeginRegex.IsMatch(line))
            return ("[redacted: PEM private key]", 1);

        var authorization = AuthorizationRegex.Replace(line, "$1\"[redacted]\"");
        if (authorization != line)
            return (authorization, 1);

        var quoted = AssignmentSecretQuotedRegex.Replace(line, "$1\"[redacted]\"");
        if (quoted != line)
            return (quoted, 1);

        var assignment = AssignmentSecretRegex.Replace(line, "$1[redacted]");
        if (assignment != line)
            return (assignment, 1);

        return (line, 0);
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
