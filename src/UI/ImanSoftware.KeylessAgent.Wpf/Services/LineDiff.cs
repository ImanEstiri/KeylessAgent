namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>The kind of one diff row.</summary>
internal enum DiffLineKind
{
    /// <summary>Present in old and new content (dimmed).</summary>
    Unchanged = 0,

    /// <summary>Only in the new content (green tint).</summary>
    Added = 1,

    /// <summary>Only in the old content (red tint).</summary>
    Removed = 2,
}

/// <summary>One row of the unified diff: text, kind and the 1-based old/new line numbers when present.</summary>
internal sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber);

/// <summary>The outcome of a line diff. When <see cref="TooLarge"/>, no LCS ran: <see cref="Lines"/> is empty,
/// <see cref="Removed"/> holds the old file's line count and <see cref="Added"/> the new file's line count.</summary>
internal sealed record DiffResult(IReadOnlyList<DiffLine> Lines, int Added, int Removed, bool TooLarge);

/// <summary>
/// Pure line diff (Appendix D): normalize line endings, too-large fast path, common prefix/suffix
/// trim, LCS dynamic-programming table on the middle part. No WPF types — unit tested directly.
/// </summary>
internal static class LineDiff
{
    /// <summary>Files with more lines than this are not diffed (TooLarge).</summary>
    public const int MaxLines = 3000;

    /// <summary>Upper bound for the LCS table; above it the middle part falls back to all-removed-then-all-added.</summary>
    private const long MaxLcsCells = 3000L * 3000L;

    /// <summary>Compares two texts line by line. CRLF and LF are the same line ending; a trailing
    /// newline difference is ignored; null means an empty file.</summary>
    /// <param name="oldText">The old content (null for a new file).</param>
    /// <param name="newText">The new content (null for a deleted file).</param>
    public static DiffResult Compare(string? oldText, string? newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        if (oldLines.Length > MaxLines || newLines.Length > MaxLines)
            return new DiffResult([], newLines.Length, oldLines.Length, TooLarge: true);

        var lines = new List<DiffLine>(oldLines.Length + newLines.Length);

        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix])
            prefix++;

        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
               && oldLines[oldLines.Length - 1 - suffix] == newLines[newLines.Length - 1 - suffix])
            suffix++;

        for (var i = 0; i < prefix; i++)
            lines.Add(new DiffLine(DiffLineKind.Unchanged, oldLines[i], i + 1, i + 1));

        AppendMiddle(lines, oldLines, newLines, prefix, prefix, suffix);

        var oldSuffixStart = oldLines.Length - suffix;
        var newSuffixStart = newLines.Length - suffix;
        for (var i = 0; i < suffix; i++)
            lines.Add(new DiffLine(DiffLineKind.Unchanged, oldLines[oldSuffixStart + i], oldSuffixStart + i + 1, newSuffixStart + i + 1));

        var added = 0;
        var removed = 0;
        foreach (var line in lines)
        {
            if (line.Kind == DiffLineKind.Added) added++;
            else if (line.Kind == DiffLineKind.Removed) removed++;
        }

        return new DiffResult(lines, added, removed, TooLarge: false);
    }

    /// <summary>Splits text into lines per step 1 of Appendix D (line-ending normalization, no trailing empty line).</summary>
    /// <param name="text">The raw text; null yields an empty array.</param>
    internal static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static void AppendMiddle(List<DiffLine> lines, string[] oldLines, string[] newLines, int oldStart, int newStart, int suffix)
    {
        var oldFirst = oldStart;
        var oldCount = oldLines.Length - suffix - oldFirst;
        var newCount = newLines.Length - suffix - newStart;

        var oldMiddle = new string[oldCount];
        Array.Copy(oldLines, oldFirst, oldMiddle, 0, oldCount);
        var newMiddle = new string[newCount];
        Array.Copy(newLines, newStart, newMiddle, 0, newCount);

        if (oldMiddle.Length == 0)
        {
            for (var j = 0; j < newMiddle.Length; j++)
                lines.Add(new DiffLine(DiffLineKind.Added, newMiddle[j], null, newStart + j + 1));
            return;
        }

        if (newMiddle.Length == 0)
        {
            for (var i = 0; i < oldMiddle.Length; i++)
                lines.Add(new DiffLine(DiffLineKind.Removed, oldMiddle[i], oldStart + i + 1, null));
            return;
        }

        if ((long)oldMiddle.Length * newMiddle.Length > MaxLcsCells)
        {
            for (var i = 0; i < oldMiddle.Length; i++)
                lines.Add(new DiffLine(DiffLineKind.Removed, oldMiddle[i], oldStart + i + 1, null));
            for (var j = 0; j < newMiddle.Length; j++)
                lines.Add(new DiffLine(DiffLineKind.Added, newMiddle[j], null, newStart + j + 1));
            return;
        }

        var table = new int[oldMiddle.Length + 1][];
        for (var row = 0; row <= oldMiddle.Length; row++)
            table[row] = new int[newMiddle.Length + 1];

        for (var i = oldMiddle.Length - 1; i >= 0; i--)
        {
            for (var j = newMiddle.Length - 1; j >= 0; j--)
            {
                table[i][j] = oldMiddle[i] == newMiddle[j]
                    ? table[i + 1][j + 1] + 1
                    : Math.Max(table[i + 1][j], table[i][j + 1]);
            }
        }

        var x = 0;
        var y = 0;
        while (x < oldMiddle.Length && y < newMiddle.Length)
        {
            if (oldMiddle[x] == newMiddle[y])
            {
                lines.Add(new DiffLine(DiffLineKind.Unchanged, oldMiddle[x], oldStart + x + 1, newStart + y + 1));
                x++;
                y++;
            }
            else if (table[x + 1][y] >= table[x][y + 1])
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, oldMiddle[x], oldStart + x + 1, null));
                x++;
            }
            else
            {
                lines.Add(new DiffLine(DiffLineKind.Added, newMiddle[y], null, newStart + y + 1));
                y++;
            }
        }

        for (; x < oldMiddle.Length; x++)
            lines.Add(new DiffLine(DiffLineKind.Removed, oldMiddle[x], oldStart + x + 1, null));

        for (; y < newMiddle.Length; y++)
            lines.Add(new DiffLine(DiffLineKind.Added, newMiddle[y], null, newStart + y + 1));
    }
}
