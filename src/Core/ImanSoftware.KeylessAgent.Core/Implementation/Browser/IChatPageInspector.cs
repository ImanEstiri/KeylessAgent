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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching fallback index and the element count, or null when nothing matched.</returns>
    Task<(int Index, int Count)?> ProbeAsync(string[] selectors, CancellationToken cancellationToken = default);

    /// <summary>
    /// The aria-pressed value of the first element matched by the fallback list, or null.
    /// </summary>
    Task<string?> GetToggleStateAsync(string[] selectors, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the input box exists and is editable, probed through the given fallback list
    /// (usually <c>InputBox</c> from the catalog).
    /// </summary>
    Task<bool> IsInputEditableAsync(string[] selectors, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a trimmed outerHTML snapshot of the first element matched by the fallback list
    /// (scripts, styles, svg removed, personal text masked), or null when nothing matched.
    /// </summary>
    Task<string?> CaptureTrimmedAsync(string[] selectors, CancellationToken cancellationToken = default);
}
