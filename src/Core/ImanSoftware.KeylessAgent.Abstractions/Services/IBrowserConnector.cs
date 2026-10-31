using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// How the browser connection was established — surfaced by doctor and the agent UI.
/// </summary>
public enum BrowserConnectionKind
{
    /// <summary>The debug port was already open: attached to the running Chrome.</summary>
    AttachedToRunningChrome,

    /// <summary>The agent started the real Chrome with the dedicated profile, then attached.</summary>
    AutoLaunchedChrome,

    /// <summary>Opt-in fallback: a Playwright-managed browser with its own profile.</summary>
    PlaywrightLaunched,
}

/// <summary>
/// A live browser attachment: the Playwright context plus the metadata the agent needs to
/// honor the dispose policy (close only our tab, leave a launched Chrome running).
/// </summary>
public sealed record BrowserAttachment
{
    /// <summary>The Playwright browser (null for persistent contexts).</summary>
    public object? Browser { get; init; }

    /// <summary>The connected or launched browser context.</summary>
    public object Context { get; init; } = null!;

    /// <summary>How the connection was established.</summary>
    public BrowserConnectionKind Kind { get; init; }

    /// <summary>Whether the agent started this Chrome process itself.</summary>
    public bool LaunchedProcess { get; init; }

    /// <summary>The Chrome process id when <see cref="LaunchedProcess"/> is true.</summary>
    public int LaunchedProcessId { get; init; }

    /// <summary>The dedicated profile directory the browser runs with.</summary>
    public string UserDataDir { get; init; } = string.Empty;
}

/// <summary>
/// Establishes the browser connection for run/doctor/chrome: probe the debug port, optionally
/// auto-launch the real Chrome with a dedicated profile, attach over CDP, and pick or open the
/// chat tab with focus. Reports every decision through the UI — never silent.
/// </summary>
public interface IBrowserConnector : IService
{
    /// <summary>
    /// Connects and returns the browser attachment plus the chosen (or newly opened) page.
    /// </summary>
    /// <param name="options">The browser section of keyless.json.</param>
    /// <param name="ui">Status messages are reported here (never silent).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="timings">Optional per-phase timing recorder (--timing); the tab-open phase is timed here.</param>
    /// <returns>The attachment and the chat page, or a <c>KA_BROWSER_CONNECTION_FAILED</c> failure
    /// whose message is actionable (including the manual-start command line when Chrome could
    /// not be found or started).</returns>
    Task<Outcome<(BrowserAttachment Attachment, object Page)>> ConnectAsync(
        BrowserOptions options,
        IAgentUserInterface ui,
        CancellationToken cancellationToken = default,
        ITimingRecorder? timings = null);
}
