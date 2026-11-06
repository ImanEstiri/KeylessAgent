using ImanSoftware.FileStorage;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;

/// <summary>
/// Generates the two workspace snapshots natively in C# — no .bat files required:
/// <para>
/// <see cref="WriteTreeViewAsync"/> reproduces <c>tree /F /A</c> (ASCII tree with files),
/// and <see cref="WriteCodeDumpAsync"/> reproduces <c>code-dump.bat</c> (one ### heading per
/// file with a fenced code block). Noise folders (bin, obj, .git, node_modules, …) are skipped
/// in both. The outputs land on the same files the old scripts produced
/// (<c>tree-view-result.txt</c>, <c>code-dump-result.md</c>) so the attachment pipeline is unchanged.
/// </para>
/// </summary>
internal sealed partial class WorkspaceAnalyzer
{
    /// <summary>File extensions included in the code dump (mirrors the reference scripts).</summary>
    internal static readonly IReadOnlyList<string> DumpExtensions =
    [
        ".razor", ".css", ".cs", ".js", ".ts", ".html", ".cshtml", ".json", ".scss", ".less",
        ".xaml", ".config", ".csproj", ".aspx", ".master", ".sql", ".txt", ".text", ".md",
        ".bat", ".ps1",
    ];

    /// <summary>Directory names excluded from the tree and the dump.</summary>
    internal static readonly IReadOnlyList<string> ExcludedDirectories =
        ["bin", "obj", "node_modules", ".git", ".vs", ".idea", "wwwroot\\lib", ".agent", "artifacts"];

    /// <summary>The highlight language for one file extension ('text' when unknown).</summary>
    internal static string LanguageFor(string extension)
        => LanguageByExtension.GetValueOrDefault(extension, "text");

    private static readonly Dictionary<string, string> LanguageByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".razor"] = "razor",
        [".cshtml"] = "cshtml",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".js"] = "javascript",
        [".ts"] = "typescript",
        [".html"] = "html",
        [".xaml"] = "xml",
        [".json"] = "json",
        [".config"] = "xml",
        [".csproj"] = "xml",
        [".aspx"] = "aspnet",
        [".master"] = "aspnet",
        [".sql"] = "sql",
        [".md"] = "markdown",
        [".bat"] = "batch",
        [".ps1"] = "powershell",
        [".txt"] = "text",
        [".text"] = "text",
    };

    private readonly IFileStorage _storage;

    public WorkspaceAnalyzer(IFileStorage storage)
    {
        _storage = storage;
    }

    /// <summary>Whether the directory (by any path segment or full suffix) is excluded.</summary>
    internal static bool IsExcludedDirectory(string directoryPath)
    {
        foreach (var excluded in ExcludedDirectories)
        {
            if (excluded.Contains('\\'))
            {
                if (directoryPath.Replace('/', '\\').EndsWith(excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                var name = Path.GetFileName(directoryPath);
                if (name.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes <c>tree-view-result.txt</c>: an ASCII tree of the workspace including files
    /// (the shape of <c>tree /F /A</c>).
    /// </summary>
    public async Task WriteTreeViewAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var builder = new System.Text.StringBuilder();
        var root = Path.GetFullPath(workspaceRoot);
        builder.AppendLine("Folder PATH listing for volume");
        builder.AppendLine(root);

        AppendDirectory(builder, root, string.Empty, cancellationToken);
        await _storage.WriteAllTextAsync(Path.Combine(root, "tree-view-result.txt"), builder.ToString(), cancellationToken);
    }

    private void AppendDirectory(System.Text.StringBuilder builder, string fullPath, string prefix, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directories = SafeEnumerate(fullPath, p => Directory.EnumerateDirectories(p))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var files = SafeEnumerate(fullPath, p => Directory.EnumerateFiles(p))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = files.Select(f => (Name: Path.GetFileName(f), Path: f, IsDirectory: false))
            .Concat(directories.Select(d => (Name: Path.GetFileName(d), Path: d, IsDirectory: true)))
            .ToList();

        for (var i = 0; i < entries.Count; i++)
        {
            var (name, path, isDirectory) = entries[i];
            var isLast = i == entries.Count - 1;
            var connector = isLast ? "\\---" : "+---";
            builder.Append(prefix).Append(connector).AppendLine(name);

            if (!isDirectory)
                continue;

            if (IsExcludedDirectory(path))
            {
                builder.Append(prefix).Append(isLast ? "    " : "|   ").AppendLine("\\   [excluded]");
                continue;
            }

            AppendDirectory(builder, path, prefix + (isLast ? "    " : "|   "), cancellationToken);
        }
    }

    /// <summary>
    /// Writes <c>code-dump-result.md</c>: one <c>### heading</c> plus a fenced code block per
    /// included source file (headings use " &gt; " folder separators like the reference script).
    /// </summary>
    public async Task WriteCodeDumpAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var builder = new System.Text.StringBuilder();
        var root = Path.GetFullPath(workspaceRoot);
        var outputPath = Path.Combine(root, "code-dump-result.md");
        var treeOutputPath = Path.Combine(root, "tree-view-result.txt");

        foreach (var file in EnumerateDumpFiles(root, outputPath, treeOutputPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(root, file);
            var heading = relative.Replace('\\', '>');
            heading = heading.Contains('>') ? string.Join(" > ", relative.Split('\\')) : relative;
            var language = LanguageByExtension.GetValueOrDefault(Path.GetExtension(file), "text");
            var fence = Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase) ? "````" : "```";

            builder.AppendLine($"### {heading}");
            builder.AppendLine();
            builder.AppendLine(fence + language);
            var content = await _storage.ReadAllTextAsync(file, cancellationToken);
            builder.AppendLine(content.IsFailure ? "[unreadable]" : content.Data!.TrimEnd('\r', '\n'));
            builder.AppendLine(fence);
            builder.AppendLine();
        }

        await _storage.WriteAllTextAsync(outputPath, builder.ToString(), cancellationToken);
    }

    private IEnumerable<string> EnumerateDumpFiles(
        string root, string outputPath, string treeOutputPath, CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            foreach (var file in SafeEnumerate(current, p => Directory.EnumerateFiles(p))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (file.Equals(outputPath, StringComparison.OrdinalIgnoreCase) ||
                    file.Equals(treeOutputPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the outputs themselves never enter the dump
                }

                if (!DumpExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue; // binaries, logs and other data do not belong in the dump
                }

                yield return file;
            }

            foreach (var directory in SafeEnumerate(current, p => Directory.EnumerateDirectories(p))
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                         .Reverse())
            {
                if (IsExcludedDirectory(directory))
                    continue;

                stack.Push(directory);
            }
        }
    }

    /// <summary>
    /// The mirror image of EnumerateDumpFiles: every workspace file that the dump SKIPS
    /// (non-text extensions) but that is not in an excluded folder and not an output —
    /// listed path-only in the snapshot context.
    /// </summary>
    internal IEnumerable<string> EnumerateNonTextFiles(
        string root, string outputPath, string treeOutputPath, CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();

            foreach (var file in SafeEnumerate(current, p => Directory.EnumerateFiles(p))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (file.Equals(outputPath, StringComparison.OrdinalIgnoreCase) ||
                    file.Equals(treeOutputPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // the outputs themselves never enter the listing
                }

                if (DumpExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    continue; // text files carry their content in the dump
                }

                yield return file;
            }

            foreach (var directory in SafeEnumerate(current, p => Directory.EnumerateDirectories(p))
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                         .Reverse())
            {
                if (IsExcludedDirectory(directory))
                    continue;

                stack.Push(directory);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string directory, Func<string, IEnumerable<string>> enumerate)
    {
        try
        {
            return enumerate(directory);
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }
}
