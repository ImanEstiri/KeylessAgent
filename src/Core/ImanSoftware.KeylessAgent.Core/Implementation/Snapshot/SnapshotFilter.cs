using System.Text;
using System.Text.RegularExpressions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;

/// <summary>
/// Noise filtering and chunking for snapshot attachments. Everything here works on strings only:
/// the files on disk are never touched.
/// </summary>
internal static partial class SnapshotFilter
{
    [GeneratedRegex(@"^(?<prefix>[| +\\\/\-]*)")]
    private static partial Regex TreeLinePrefixRegex();

    /// <summary>
    /// Drops tree lines whose folder/file name matches any exclude pattern (bin, obj, .git, …).
    /// Also drops a parent's nested block once the parent folder itself is excluded.
    /// </summary>
    internal static List<string> FilterTreeLines(IEnumerable<string> lines, IReadOnlyList<string> patterns)
    {
        var result = new List<string>();
        var excludedDepths = new List<(int Depth, string Folder)>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
                continue;

            // A tree line is either "|   +---folder", "|   \---folder" or "|   |   file.cs".
            // The name is what follows the last "---" for folders, and the leading decoration run for files.
            var markerIndex = line.LastIndexOf("---", StringComparison.Ordinal);
            int nameStart;
            bool isFolder;
            if (markerIndex >= 0)
            {
                var marker = markerIndex > 0 ? line[markerIndex - 1] : '\0';
                isFolder = marker is '+' or '\\';
                nameStart = markerIndex + 3;
            }
            else
            {
                var match = TreeLinePrefixRegex().Match(line);
                nameStart = match.Length;
                isFolder = false;
            }

            var name = line[nameStart..].Trim();
            var depth = nameStart;

            // Tree /F /A depth model: a FOLDER line starts a new sibling entry (pop every
            // exclusion at or deeper than it, then check the folder's own name), while a FILE
            // line is a child of the deepest folder at its marker column (drop it while
            // covered by an exclusion; pop exclusions strictly shallower than it, i.e. the
            // tree returned above the excluded folder). The original single depth <= d.Depth
            // rule consumed the exclusion on the first child, so the contents of ROOT-LEVEL
            // bin/obj folders (children at the same marker column) leaked into the tree.
            if (isFolder)
            {
                excludedDepths.RemoveAll(d => depth <= d.Depth);
                if (excludedDepths.Any(d => depth > d.Depth))
                    continue;

                if (name.Length > 0 && IsExcluded(name, patterns))
                {
                    excludedDepths.Add((depth, name));
                    continue;
                }
            }
            else
            {
                excludedDepths.RemoveAll(d => depth < d.Depth);
                if (excludedDepths.Any(d => depth >= d.Depth))
                    continue;
            }

            result.Add(line);
        }

        return result;
    }

    /// <summary>
    /// Drops dump sections (delimited by <c>### heading</c> lines) whose FILE NAME matches
    /// any wildcard pattern (case-insensitive <c>*</c>) — the generated/vendored denylist
    /// (minified bundles, lock files, designer/migration snapshots). The heading itself is
    /// kept out entirely, exactly like the node_modules directory exclusion.
    /// </summary>
    internal static string FilterDumpSectionsByFileName(string dump, IReadOnlyList<string> filePatterns)
    {
        if (filePatterns.Count == 0)
            return dump;

        var lines = dump.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var skipping = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var heading = line["### ".Length..].Trim();
                skipping = MatchesFileWildcard(LastHeadingSegment(heading), filePatterns);
            }

            if (!skipping)
                kept.Add(line);
        }

        return string.Join("\n", kept).Trim('\n');
    }

    /// <summary>
    /// Caps every section's fenced code content at <paramref name="maxFileChars"/>: oversized
    /// files keep their <c>### heading</c> (existence stays visible in the attachment) but the
    /// content is replaced by a placeholder pointing at the file-read tool.
    /// </summary>
    internal static string CapDumpSectionSizes(string dump, int maxFileChars)
    {
        if (maxFileChars <= 0)
            return dump;

        var lines = dump.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>();
        var i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("### ", StringComparison.Ordinal))
            {
                result.Add(lines[i]);
                i++;
                continue;
            }

            // A section: heading, blank, fence, content…, fence (blank follows, if any).
            var sectionStart = i;
            var fenceCount = 0;
            var contentStart = -1;
            var contentEnd = -1;
            var j = i + 1;
            for (; j < lines.Length; j++)
            {
                if (lines[j].StartsWith("```", StringComparison.Ordinal) ||
                    lines[j].StartsWith("````", StringComparison.Ordinal))
                {
                    fenceCount++;
                    if (fenceCount == 1)
                        contentStart = j + 1;
                    else
                    {
                        contentEnd = j;
                        break;
                    }
                }
            }

            if (fenceCount < 2 || contentStart < 0 || contentEnd < 0)
            {
                // Malformed section: keep verbatim to the next heading (or end).
                while (i <= sectionStart && i < lines.Length)
                    result.Add(lines[i++]);
                continue;
            }

            var contentLength = 0;
            for (var k = contentStart; k < contentEnd; k++)
                contentLength += lines[k].Length + 1;

            if (contentLength > maxFileChars)
            {
                // Oversized: heading + fences remain, the content becomes the placeholder.
                var sizeKb = Math.Max(1, contentLength / 1024);
                result.Add(lines[i]); // heading
                for (var k = i + 1; k < contentStart - 1; k++)
                    result.Add(lines[k]);
                result.Add(lines[contentStart - 1]); // opening fence
                result.Add($"[file too large to attach: {sizeKb} KB - use the file-read tool if its content is needed]");
                result.Add(lines[contentEnd]); // closing fence
            }
            else
            {
                for (var k = i; k <= contentEnd; k++)
                    result.Add(lines[k]);
            }

            i = contentEnd + 1;
        }

        return string.Join("\n", result).Trim('\n');
    }

    /// <summary>Case-insensitive <c>*</c> wildcard match against a file name.</summary>
    internal static bool MatchesFileWildcard(string fileName, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.Length == 0)
                continue;

            var regex = "^" + Regex
                .Escape(pattern.Trim())
                .Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            if (Regex.IsMatch(fileName, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Drops dump sections (delimited by <c>### heading</c> lines) whose heading's file name
    /// matches any exclude pattern (previous outputs, generated reports).
    /// </summary>
    internal static string FilterDumpSections(string dump, IReadOnlyList<string> headingPatterns)
    {
        var lines = dump.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var skipping = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                var heading = line["### ".Length..].Trim();
                skipping = IsExcluded(LastHeadingSegment(heading), headingPatterns);
            }

            if (!skipping)
                kept.Add(line);
        }

        return string.Join("\n", kept).Trim('\n');
    }

    /// <summary>
    /// Splits a dump into parts no longer than <paramref name="maxChars"/> each,
    /// cutting only at <c>### heading</c> boundaries. A single oversized section becomes its own part.
    /// </summary>
    internal static List<string> ChunkDump(string dump, int maxChars)
    {
        var parts = new List<string>();
        if (dump.Length <= maxChars)
        {
            parts.Add(dump);
            return parts;
        }

        var current = new StringBuilder();
        foreach (var section in SplitSections(dump))
        {
            if (current.Length > 0 && current.Length + section.Length > maxChars)
            {
                parts.Add(current.ToString().Trim('\n'));
                current.Clear();
            }

            if (section.Length > maxChars)
            {
                parts.Add(section.Trim('\n'));
                continue;
            }

            current.Append(section);
        }

        if (current.Length > 0)
            parts.Add(current.ToString().Trim('\n'));

        return parts;
    }

    /// <summary>
    /// "src > Core > Program" → "Program" (the dump uses " > " as folder separator in headings).
    /// </summary>
    internal static string LastHeadingSegment(string heading)
    {
        var segments = heading.Split('>');
        return segments[^1].Trim();
    }

    private static IEnumerable<string> SplitSections(string dump)
    {
        var lines = dump.Replace("\r\n", "\n").Split('\n');
        var current = new StringBuilder();

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

    private static bool IsExcluded(string token, IReadOnlyList<string> patterns)
        => patterns.Any(p => !string.IsNullOrWhiteSpace(p) &&
                             token.Equals(p.Trim(), StringComparison.OrdinalIgnoreCase));
}
