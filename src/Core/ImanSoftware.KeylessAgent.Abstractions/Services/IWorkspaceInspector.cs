using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// The git-related capabilities of the chosen workspace, probed ONCE per session and reused
/// for every git decision (branch guard, menus, commit, push).
/// </summary>
/// <param name="GitInstalled">Whether the git executable is installed and callable.</param>
/// <param name="IsRepository">Whether the workspace is inside a git work tree.</param>
/// <param name="CurrentBranch">The current branch name ("main", an agent branch, "HEAD" when undeterminable).</param>
/// <param name="IsAgentBranch">Whether the current branch matches the configured agent-branch prefix.</param>
public sealed record WorkspaceCapabilities(
    bool GitInstalled,
    bool IsRepository,
    string CurrentBranch,
    bool IsAgentBranch)
{
    /// <summary>
    /// NO-GIT MODE: git is not installed. File commands still work (with backups), but there
    /// is no branch protection, no commits, no push and no git-based change tracking.
    /// </summary>
    public bool NoGitMode => !GitInstalled;

    /// <summary>Whether git-based change tracking is available.</summary>
    public bool CanTrackWithGit => GitInstalled && IsRepository;
}

/// <summary>
/// Probes the workspace capabilities once per session. Implementations must be cheap to call
/// repeatedly (implementations cache the result).
/// </summary>
public interface IWorkspaceInspector : IService
{
    /// <summary>
    /// Returns the workspace capabilities (cached per instance).
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="options">The configured git options (branch prefix).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The capabilities snapshot.</returns>
    Task<Outcome<WorkspaceCapabilities>> InspectAsync(
        string workspaceRoot,
        ImanSoftware.KeylessAgent.Abstractions.Models.Configuration.GitOptions options,
        CancellationToken cancellationToken = default);
}
