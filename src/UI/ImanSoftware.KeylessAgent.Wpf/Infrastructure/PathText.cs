namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>Pure helpers for displaying long file-system paths.</summary>
public static class PathText
{
    /// <summary>Default display length before trimming.</summary>
    public const int DefaultMaxLength = 48;

    /// <summary>Trims a path in the middle ("G:\Projects\…\demo") so both ends stay readable.</summary>
    /// <param name="path">The full path; null yields an empty string.</param>
    /// <param name="maxLength">Display length at which trimming starts.</param>
    public static string MiddleTrim(string? path, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(path) || path.Length <= maxLength)
            return path ?? string.Empty;

        var half = Math.Max(1, (maxLength - 1) / 2);
        return string.Concat(path.AsSpan(0, half), "…", path.AsSpan(path.Length - half));
    }
}
