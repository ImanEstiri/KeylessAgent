using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Abstractions.Models.Agent;

/// <summary>
/// The outcome of one agent turn (one user request), reported to the UI at the end.
/// </summary>
public sealed record AgentTurnResult
{
    /// <summary>
    /// The branch the turn worked on.
    /// </summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>
    /// The 1-based request number within the session.
    /// </summary>
    public int Turn { get; init; }

    /// <summary>
    /// Commit summaries made during the turn.
    /// </summary>
    public IReadOnlyList<string> Commits { get; init; } = [];

    /// <summary>
    /// The summary from the final <c>@@done</c>, when present.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// True when the model ended the turn with <c>@@done</c>.
    /// </summary>
    public bool CompletedByDone { get; init; }

    /// <summary>
    /// Why the turn stopped early (iteration limit, format retries exhausted, error, cancelled); null when it ended normally.
    /// </summary>
    public string? StoppedReason { get; init; }

    /// <summary>
    /// Error message when the turn failed before any model interaction.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The stable KA_ code for the failure, when known.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// True when the turn ran in dry-run mode (nothing was executed).
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The workspace files changed during this turn (added/modified/deleted/renamed).
    /// </summary>
    public IReadOnlyList<ChangedPath> ChangeSet { get; init; } = [];

    /// <summary>
    /// The per-phase timing table of this turn (the same text that is appended to the turn log).
    /// </summary>
    public string? Timings { get; init; }

    /// <summary>
    /// How many runcommand executions ran without asking the user (auto-approved by tier).
    /// </summary>
    public int AutoApprovedCommands { get; init; }

    /// <summary>
    /// How many runcommand executions asked the user for confirmation (accepted or declined).
    /// </summary>
    public int ConfirmedCommands { get; init; }

    /// <summary>
    /// Output of <c>git diff --stat &lt;base&gt;...HEAD</c> after the turn; empty when git
    /// is unavailable, the turn was dry-run, or git failed (never fails the turn).
    /// </summary>
    public string DiffStat { get; init; } = "";
}
