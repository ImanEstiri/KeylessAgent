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
