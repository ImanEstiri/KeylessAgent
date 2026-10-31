namespace ImanSoftware.KeylessAgent.Abstractions.Services;

using ImanSoftware.Outcomes;

/// <summary>
/// How a selector behaved against the live page during a doctor run.
/// </summary>
public enum SelectorCheckStatus
{
    /// <summary>At least one fallback selector matched.</summary>
    Matched,

    /// <summary>No fallback selector matched although the element should exist.</summary>
    NotFound,

    /// <summary>The element legitimately cannot exist right now (e.g. Stop while idle).</summary>
    NotApplicableNow,
}

/// <summary>
/// The result of checking one selector key of selectors.json.
/// </summary>
/// <param name="Key">The selectors.json key (InputBox, SendButton, …).</param>
/// <param name="Status">Matched / NotFound / NotApplicableNow.</param>
/// <param name="FallbackIndex">Which fallback selector matched (−1 when none did).</param>
/// <param name="MatchCount">How many elements the matching selector found.</param>
/// <param name="Detail">Human-readable extra information (English).</param>
public sealed record SelectorCheck(
    string Key,
    SelectorCheckStatus Status,
    int FallbackIndex,
    int MatchCount,
    string Detail);

/// <summary>
/// One full doctor report over the live chat page.
/// </summary>
/// <param name="PageUrl">The inspected page URL.</param>
/// <param name="LoggedIn">Whether the login page is NOT shown.</param>
/// <param name="DeepThinkPressed">The DeepThink toggle's aria-pressed value.</param>
/// <param name="SearchPressed">The Search toggle's aria-pressed value.</param>
/// <param name="InputAcceptsText">Whether the input box is editable.</param>
/// <param name="Checks">One check per selector key.</param>
/// <param name="AllRequiredMatched">Whether every selector required for the current state matched.</param>
/// <param name="CapturedFiles">Paths of the HTML snapshots written by --capture (may be empty).</param>
public sealed record ChatDiagnosticsReport(
    string PageUrl,
    bool LoggedIn,
    string? DeepThinkPressed,
    string? SearchPressed,
    bool InputAcceptsText,
    IReadOnlyList<SelectorCheck> Checks,
    bool AllRequiredMatched,
    IReadOnlyList<string> CapturedFiles);

/// <summary>
/// Live selector check: attaches to the user's Chrome, opens the DeepSeek chat page in a new
/// tab and reports how every selector in selectors.json behaves — without ever sending a
/// message or changing the chat. The logic lives in Core so a future GUI can reuse it.
/// </summary>
public interface IChatDiagnostics : IService
{
    /// <summary>
    /// Runs the inspection.
    /// </summary>
    /// <param name="workspaceRoot">The workspace (for the selectors file and capture defaults).</param>
    /// <param name="browser">The browser section of keyless.json.</param>
    /// <param name="captureDirectory">When set, trimmed HTML snapshots of the interesting areas are written here.</param>
    /// <param name="watch">When true, waits for a manually sent prompt and captures the generating and finished states.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report, or a <c>KA_BROWSER_*</c> failure when Chrome cannot be reached.</returns>
    Task<Outcome<ChatDiagnosticsReport>> InspectAsync(
        string workspaceRoot,
        ImanSoftware.KeylessAgent.Abstractions.Models.Configuration.BrowserOptions browser,
        string? captureDirectory,
        bool watch,
        CancellationToken cancellationToken = default);
}
