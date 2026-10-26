namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// The probing seam behind <c>keyless doctor</c>: checks selector fallback lists against the
/// live page and captures trimmed snapshots. Implemented by Playwright (real) and faked in tests.
/// </summary>
internal interface IChatPageInspector
{
    /// <summary>The inspected page URL.</summary>
    string Url { get; }

    /// <summary>
    /// Probes an ordered fallback list.
    /// </summary>
    /// <param name="selectors">The fallback list (first match wins).</param>
    /// <returns>The matching fallback index and the element count, or null when nothing matched.</returns>
    (int Index, int Count)? Probe(string[] selectors);

    /// <summary>
    /// The aria-pressed value of the first element matched by the fallback list, or null.
    /// </summary>
    string? GetToggleState(string[] selectors);

    /// <summary>Whether the input box exists and is editable.</summary>
    bool IsInputEditable();

    /// <summary>
    /// Captures a trimmed outerHTML snapshot of the first element matched by the fallback list
    /// (scripts, styles, svg and long text removed), or null when nothing matched.
    /// </summary>
    string? CaptureTrimmed(string[] selectors);
}

/// <summary>
/// Diagnostics-only HTML trimming: removes scripts, styles, inline svg and long text runs
/// (message text becomes "...") so a captured snapshot is safe to attach to a bug report.
/// </summary>
internal static class HtmlTrimmer
{
    /// <summary>
    /// Trims an HTML fragment for bug reports.
    /// </summary>
    /// <param name="html">Raw HTML.</param>
    /// <returns>Trimmed HTML, or an empty string for empty input.</returns>
    public static string Trim(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var text = System.Text.RegularExpressions.Regex.Replace(
            html, "<script\\b[^>]*>.*?</script>", "<script removed>", System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        text = System.Text.RegularExpressions.Regex.Replace(
            text, "<style\\b[^>]*>.*?</style>", "<style removed>", System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        text = System.Text.RegularExpressions.Regex.Replace(
            text, "<svg\\b[^>]*>.*?</svg>", "<svg/>", System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        text = System.Text.RegularExpressions.Regex.Replace(
            text, "<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1));

        // Personal text never leaves the machine: any text run over 60 chars becomes "...".
        text = System.Text.RegularExpressions.Regex.Replace(
            text, ">[^<>]{61,}<", ">...", System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1));

        return text;
    }
}
