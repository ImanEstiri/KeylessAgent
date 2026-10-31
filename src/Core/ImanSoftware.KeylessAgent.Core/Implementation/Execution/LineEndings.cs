using System.IO.Enumeration;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Line-ending rules for workspace files: workspace files are usually CRLF, model text is LF.
/// Executors detect the existing file's dominant ending and keep it; new files resolve their
/// ending from .editorconfig / .gitattributes, then from the dominant ending of sibling files.
/// </summary>
internal static class LineEndings
{
    /// <summary>
    /// Detects the dominant ending: CRLF or LF (mixed files keep the majority; a tie means LF).
    /// Files without any line break report LF.
    /// </summary>
    /// <param name="text">The file content.</param>
    /// <returns>"\r\n" or "\n".</returns>
    internal static string Detect(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            if (i > 0 && text[i - 1] == '\r')
                crlf++;
            else
                lf++;
        }

        return crlf > lf ? "\r\n" : "\n";
    }

    /// <summary>
    /// Converts every line break (CRLF or lone CR) to a plain LF.
    /// </summary>
    /// <param name="text">Any text.</param>
    /// <returns>LF-only text.</returns>
    internal static string NormalizeToLf(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// Converts LF-normalized text to the target ending.
    /// </summary>
    /// <param name="lfText">LF-only text (see <see cref="NormalizeToLf"/>).</param>
    /// <param name="ending">"\r\n" or "\n".</param>
    /// <returns>Text using the target ending.</returns>
    internal static string Apply(string lfText, string ending)
        => ending == "\r\n" ? lfText.Replace("\n", "\r\n") : lfText;

    /// <summary>
    /// Resolves the ending for a NEW file: .editorconfig (end_of_line) for that path, else
    /// .gitattributes (eol=…) for that path, else the dominant ending of sibling files in the
    /// same folder, else <see cref="Environment.NewLine"/>.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="fullPath">The absolute path of the new file.</param>
    /// <returns>"\r\n" or "\n".</returns>
    internal static string ResolveNewFileEnding(string workspaceRoot, string fullPath)
    {
        var editorConfig = ResolveFromEditorConfig(workspaceRoot, fullPath);
        if (editorConfig is not null)
            return editorConfig;

        var gitAttributes = ResolveFromGitAttributes(workspaceRoot, fullPath);
        if (gitAttributes is not null)
            return gitAttributes;

        var dominant = DetectDominantInFolder(Path.GetDirectoryName(fullPath)!);
        if (dominant is not null)
            return dominant;

        return Environment.NewLine == "\r\n" ? "\r\n" : "\n";
    }

    private static string? ResolveFromEditorConfig(string workspaceRoot, string fullPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(fullPath)!);
        for (var current = directory; current is not null &&
             current.FullName.StartsWith(Path.GetFullPath(workspaceRoot), StringComparison.OrdinalIgnoreCase);
             current = current.Parent)
        {
            var configPath = Path.Combine(current.FullName, ".editorconfig");
            if (!File.Exists(configPath))
                continue;

            var relative = Path.GetRelativePath(current.FullName, fullPath).Replace('\\', '/');
            var fileName = Path.GetFileName(fullPath);
            string? result = null;

            string? currentSection = null;
            foreach (var rawLine in File.ReadAllLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                    continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    currentSection = line[1..^1].Trim();
                    continue;
                }

                if (currentSection is null)
                    continue;

                var separator = line.IndexOf('=');
                if (separator < 0)
                    continue;

                var key = line[..separator].Trim();
                if (!string.Equals(key, "end_of_line", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (SectionMatches(currentSection, relative, fileName))
                {
                    var value = line[(separator + 1)..].Trim().ToLowerInvariant();
                    if (value == "crlf")
                        result = "\r\n";
                    else if (value == "lf")
                        result = "\n";
                }
            }

            if (result is not null)
                return result;
        }

        return null;
    }

    private static bool SectionMatches(string section, string relativePath, string fileName)
    {
        if (section is "*")
            return true;

        foreach (var glob in section.Split(','))
        {
            var pattern = glob.Trim();
            if (pattern.Length == 0)
                continue;

            if (FileSystemName.MatchesSimpleExpression(pattern, fileName) ||
                FileSystemName.MatchesSimpleExpression(pattern, relativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ResolveFromGitAttributes(string workspaceRoot, string fullPath)
    {
        var attributesPath = Path.Combine(workspaceRoot, ".gitattributes");
        if (!File.Exists(attributesPath))
            return null;

        var relative = Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
        foreach (var rawLine in File.ReadAllLines(attributesPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                continue;

            var pattern = parts[0].TrimStart('/');
            if (!FileSystemName.MatchesSimpleExpression(pattern, relative) &&
                !(pattern.StartsWith("*.", StringComparison.Ordinal) &&
                  FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(relative))))
            {
                continue;
            }

            foreach (var attribute in parts[1..])
            {
                if (attribute.Equals("eol=crlf", StringComparison.OrdinalIgnoreCase))
                    return "\r\n";
                if (attribute.Equals("eol=lf", StringComparison.OrdinalIgnoreCase))
                    return "\n";
            }
        }

        return null;
    }

    private static string? DetectDominantInFolder(string directory)
    {
        try
        {
            var crlf = 0;
            var lf = 0;
            foreach (var file in Directory.EnumerateFiles(directory).Take(20))
            {
                // Only source-like files vote: extension-less dot/config files (editorconfig,
                // gitattributes, gitignore) carry arbitrary text whose ending proves nothing.
                var extension = Path.GetExtension(file);
                if (extension.Length == 0 || extension == ".editorconfig" || extension == ".gitattributes" ||
                    extension == ".gitignore" || extension == ".bat" || extension == ".cmd")
                {
                    continue;
                }

                using var stream = File.OpenRead(file);
                var buffer = new byte[Math.Min(stream.Length, 64 * 1024)];
                var read = stream.Read(buffer, 0, buffer.Length);
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                        continue;

                    if (i > 0 && buffer[i - 1] == (byte)'\r')
                        crlf++;
                    else
                        lf++;
                }
            }

            if (crlf == 0 && lf == 0)
                return null;

            return crlf > lf ? "\r\n" : "\n";
        }
        catch
        {
            return null;
        }
    }
}
