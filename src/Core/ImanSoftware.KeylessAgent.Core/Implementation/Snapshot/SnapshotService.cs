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

        // The baseline powers the `changed` context mode: it is captured here ONLY when it
        // does not exist yet (initial state), and afterwards refreshed by the context BUILD
        // itself (diff-then-refresh, so a changed attachment always covers exactly the
        // changes since the previous context build). A failed capture only means the next
        // changed build reports nothing.
        if (!_storage.FileExists(Path.Combine(workspaceRoot, BaselineRelativePath)))
            await CaptureBaselineAsync(workspaceRoot);

        return Outcome.Successful();
    }

    /// <summary>
    /// Captures the workspace state under <c>.agent/snapshot-baseline.json</c>: git porcelain
    /// when git answers, a path/size/mtime index otherwise. Called when no baseline exists
    /// (initial capture) and after a changed-context build (refresh after use). Best-effort.
    /// </summary>
    private async Task CaptureBaselineAsync(string workspaceRoot)
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
    public async Task<Outcome<IReadOnlyList<SnapshotAttachment>>> BuildAttachmentsAsync(
        string workspaceRoot, KeylessOptions options,
        ContextMode mode, string? requestText = null,
        IReadOnlyList<string>? explicitPaths = null,
        Func<string, object, Task>? logEvent = null)
    {
        if (mode == ContextMode.None)
            return Outcome.Successful<IReadOnlyList<SnapshotAttachment>>([]);

        var ceiling = Math.Min(options.Snapshot.MaxTotalAttachmentChars, MaxAttachmentChars);

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

        var filter = options.Snapshot.Filter;
        var tree = string.Join("\n", SnapshotFilter.FilterTreeLines(
            treeText.Data.Replace("\r\n", "\n").Split('\n'), filter.ExcludeTreePatterns)) + "\n";

        var body = new System.Text.StringBuilder();
        var fileCount = 0;

        if (mode == ContextMode.Snapshot)
        {
            var dumpText = await _storage.ReadAllTextAsync(dumpPath);
            if (dumpText.IsFailure)
                return Outcome.Failure<IReadOnlyList<SnapshotAttachment>>(dumpText.Error);

            var dump = SnapshotFilter.FilterDumpSections(dumpText.Data, filter.ExcludeDumpHeadings);
            var sanitizedDump = SnapshotFilter.CapDumpSectionSizes(dump, options.Snapshot.MaxDumpedFileChars);
            sanitizedDump = SnapshotFilter.FilterDumpSectionsByFileName(sanitizedDump, filter.ExcludeFilePatterns);

            var nonText = NonTextPaths(workspaceRoot, treePath, dumpPath);
            body.AppendLine("## tree");
            body.Append(tree);
            body.AppendLine("## files");
            body.AppendLine(sanitizedDump.Trim('\n'));
            if (nonText.Count > 0)
            {
                body.AppendLine("[non-text files (path only)]");
                foreach (var p in nonText)
                    body.AppendLine(p);
            }

            fileCount = CountSections(sanitizedDump) + nonText.Count;
        }
        else if (mode == ContextMode.Changed)
        {
            var changes = await DiffAgainstBaselineAsync(workspaceRoot);
            if (changes is null || changes.Count == 0)
                return Outcome.Successful<IReadOnlyList<SnapshotAttachment>>([]); // nothing since the last build

            fileCount = changes.Count;
            body.AppendLine("## changes");
            foreach (var line in changes)
                body.AppendLine(line);

            body.AppendLine("## files");
            foreach (var (kind, relativePath) in ParseChangeLines(changes))
            {
                body.AppendLine($"### {relativePath.Replace('\\', '/')}");
                if (kind == "deleted")
                {
                    body.AppendLine("[deleted since the last context build]");
                }
                else if (!IsTextFile(relativePath))
                {
                    body.AppendLine("[non-text file: path only]");
                }
                else
                {
                    var resolved = new PathSandbox(workspaceRoot).Resolve(relativePath);
                    var fileContent = resolved.IsSuccess
                        ? await _storage.ReadAllTextAsync(resolved.Data)
                        : Outcome.Failure<string>(resolved.Error);

                    if (fileContent.IsFailure)
                        body.AppendLine(resolved.IsFailure
                            ? $"[skipped: {resolved.Error?.Message}]"
                            : "[unreadable]");
                    else
                    {
                        body.AppendLine("```" + WorkspaceAnalyzer.LanguageFor(Path.GetExtension(relativePath)));
                        body.AppendLine(fileContent.Data.TrimEnd('\r', '\n'));
                        body.AppendLine("```");
                    }
                }

                body.AppendLine();
            }

            // Refresh after use: the NEXT changed build covers exactly what changes from here.
            await CaptureBaselineAsync(workspaceRoot);
        }
        else // ContextMode.Files
        {
            var sandbox = new PathSandbox(workspaceRoot);
            var paths = (explicitPaths ?? []).Take(options.Snapshot.MaxContextFiles).ToList();
            fileCount = paths.Count;
            foreach (var relativePath in paths)
            {
                var resolved = sandbox.Resolve(relativePath);
                if (resolved.IsFailure)
                {
                    // A rejected path is NEVER echoed back (the reason only, no path, no
                    // content): nothing outside the workspace leaks into the attachment.
                    body.AppendLine("[skipped: path rejected by the workspace sandbox]");
                    body.AppendLine();
                    continue;
                }

                body.AppendLine($"### {relativePath.Replace('\\', '/')}");
                var fileContent = await _storage.ReadAllTextAsync(resolved.Data);
                if (fileContent.IsFailure)
                    body.AppendLine("[unreadable]");
                else
                {
                    body.AppendLine("```" + WorkspaceAnalyzer.LanguageFor(Path.GetExtension(relativePath)));
                    body.AppendLine(fileContent.Data.TrimEnd('\r', '\n'));
                    body.AppendLine("```");
                }

                body.AppendLine();
            }
        }

        // AppendLine writes CRLF on Windows while the tree/dump inputs are LF: normalize so
        // the pipeline (budget sections, markers, masking) works on one consistent ending.
        var rawBody = body.ToString().Replace("\r\n", "\n");
        rawBody = SnapshotFilter.CapDumpSectionSizes(rawBody, options.Snapshot.MaxDumpedFileChars);
        rawBody = SnapshotFilter.FilterDumpSectionsByFileName(rawBody, filter.ExcludeFilePatterns);
        var (finalBody, droppedFiles, maskedValues) = SanitizeSensitiveContent(
            rawBody, filter.SensitiveFilePatterns, filter.MaskSensitiveLines);

        // ONE sanitize pass: a second pass would demask "ApiKey": "[redacted]" back to
        // [redacted] through the generic value regex. The marker is inserted after the
        // pass so the counts are final.
        if (droppedFiles + maskedValues > 0)
        {
            var marker = $"[{droppedFiles} files filtered and {maskedValues} values masked as sensitive]\n";
            var treeMarker = "## tree\n";
            finalBody = finalBody.Contains(treeMarker, StringComparison.Ordinal)
                ? finalBody.Replace(treeMarker, treeMarker + marker, StringComparison.Ordinal)
                : marker + finalBody;
        }

        var header = $"[CONTEXT mode={mode.ToString().ToLowerInvariant()} files={fileCount} generated={DateTime.UtcNow:O}]\n\n";
        var content = header + finalBody;

        if (content.Length > ceiling)
        {
            var reduced = BuildBudgetLimitedDump(finalBody, requestText, out var omittedFiles, out var omittedChars);
            content = header + reduced;
            if (logEvent is not null)
                await logEvent("context-budget-exceeded", new
                {
                    Mode = mode.ToString(),
                    OriginalChars = content.Length,
                    Ceiling = ceiling,
                    OmittedFiles = omittedFiles,
                    OmittedChars = omittedChars,
                });
        }

        return Outcome.Successful<IReadOnlyList<SnapshotAttachment>>(
            [new SnapshotAttachment("workspace-context.md", SnapshotAttachmentKind.WorkspaceContext, content)]);
    }

    private static int CountSections(string dump)
        => dump.Split('\n').Count(l => l.StartsWith("### ", StringComparison.Ordinal));

    private static bool IsTextFile(string relativePath)
        => WorkspaceAnalyzer.DumpExtensions.Contains(
            Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<(string Kind, string Path)> ParseChangeLines(IReadOnlyList<string> lines)
    {
        var result = new List<(string Kind, string Path)>();
        foreach (var line in lines)
        {
            var space = line.IndexOf(' ');
            if (space > 0)
                result.Add((line[..space], line[(space + 1)..]));
        }

        return result;
    }

    /// <summary>
    /// Non-dump workspace files (images, binaries outside excluded folders) listed as
    /// path-only lines in the snapshot context. Mirrors EnumerateDumpFiles with the
    /// extension filter inverted.
    /// </summary>
    private IReadOnlyList<string> NonTextPaths(string workspaceRoot, string treePath, string dumpPath)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var result = new List<string>();
        foreach (var file in _analyzer.EnumerateNonTextFiles(root, treePath, dumpPath, CancellationToken.None))
        {
            var relative = Path.GetRelativePath(root, file);
            result.Add(relative.Replace('\\', '/'));
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// Diffs the CURRENT workspace state against the persisted baseline and returns the
    /// "<kind> <path>" lines; null when there is no baseline or the diff failed.
    /// </summary>
    private async Task<List<string>?> DiffAgainstBaselineAsync(string workspaceRoot)
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

        return diff.Data.Select(c => $"{c.Kind} {c.Path}").ToList();
    }

    /// <summary>
    /// The size-budget dump: files REFERENCED by the turn's request text (file name or path,
    /// case-insensitive) keep their full content; everything else becomes one omission note
    /// PER FOLDER ("[omitted: 3 files under src/Core/ (182 KB total) … use the file-read
    /// tool …]"). The tree stays attached regardless — it is cheap and essential for
    /// navigation, which is why the dump alone is reduced here.
    /// </summary>
    internal static string BuildBudgetLimitedDump(
        string dump, string? requestText, out int omittedFiles, out long omittedChars)
    {
        omittedFiles = 0;
        omittedChars = 0;

        var sections = new List<(string Heading, string FileName, string Folder, string Section, bool Referenced)>();
        foreach (var section in SplitSections(dump))
        {
            var headingLine = section.Split('\n').FirstOrDefault(l => l.StartsWith("### ", StringComparison.Ordinal));
            var heading = headingLine is null ? string.Empty : headingLine["### ".Length..].Trim();
            var fileName = HeadingFileName(heading);
            var folderSegments = heading.Split(" > ", StringSplitOptions.RemoveEmptyEntries);
            var folder = folderSegments.Length > 1
                ? string.Join('/', folderSegments.Take(folderSegments.Length - 1))
                : "(workspace root)";

            var referenced = requestText is not null && IsReferencedByRequest(requestText, heading, fileName);
            sections.Add((heading, fileName, folder, section, referenced));
        }

        var kept = new System.Text.StringBuilder();
        var omittedByFolder = new List<(string Folder, int Count, long Chars)>();
        foreach (var group in sections.GroupBy(s => s.Folder))
        {
            foreach (var entry in group.Where(s => s.Referenced))
                kept.Append(entry.Section);

            var omitted = group.Where(s => !s.Referenced).ToList();
            if (omitted.Count == 0)
                continue;

            omittedFiles += omitted.Count;
            omittedChars += omitted.Sum(o => (long)o.Section.Length);
            omittedByFolder.Add((group.Key, omitted.Count, omitted.Sum(o => (long)o.Section.Length)));
        }

        foreach (var (folder, count, chars) in omittedByFolder.OrderBy(o => o.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var sizeKb = Math.Max(1, (int)(chars / 1024));
            kept.AppendLine(count == 1
                ? $"[omitted: 1 file under {folder}/ ({sizeKb} KB) - over the attachment size budget; use the file-read tool if its content is needed]"
                : $"[omitted: {count} files under {folder}/ ({sizeKb} KB total) - over the attachment size budget; use the file-read tool if their content is needed]");
        }

        return kept.ToString().Trim('\n');
    }

    private static bool IsReferencedByRequest(string requestText, string heading, string fileName)
    {
        if (requestText.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            return true;

        var slashPath = heading.Replace(" > ", "/");
        if (requestText.Contains(slashPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return requestText.Contains(heading.Replace(" > ", "\\"), StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SplitSections(string dump)
    {
        var lines = dump.Replace("\r\n", "\n").Split('\n');
        var current = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal) && current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }

            current.Append(line).Append('\n');
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static readonly string StashRelativePath = Path.Combine(".agent", "snapshot-changes.json");

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
