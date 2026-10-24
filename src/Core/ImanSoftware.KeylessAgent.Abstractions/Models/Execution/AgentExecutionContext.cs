using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Abstractions.Models.Execution;

/// <summary>
/// Everything a command executor needs about the current run.
/// </summary>
public sealed record AgentExecutionContext
{
    /// <summary>
    /// Absolute path of the workspace root (the git repository the agent works in).
    /// </summary>
    public string WorkspaceRoot { get; init; } = string.Empty;

    /// <summary>
    /// The effective configuration.
    /// </summary>
    public KeylessOptions Options { get; init; } = new();

    /// <summary>
    /// Dry-run mode: print what would happen, execute nothing.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// The UI contract to talk to the user (say, ask, confirm, diff).
    /// </summary>
    public IAgentUserInterface Ui { get; init; } = null!;

    /// <summary>
    /// Cancellation token for the current turn.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }
}
