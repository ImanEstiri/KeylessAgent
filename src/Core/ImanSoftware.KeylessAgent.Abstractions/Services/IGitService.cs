using ImanSoftware.KeylessAgent.Abstractions.Models.Git;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// All git operations the agent performs, as short-lived processes run inside the workspace.
/// The agent never force-pushes, resets shared branches, deletes branches, merges or rebases.
/// </summary>
public interface IGitService : IService
{
    /// <summary>
    /// Collects repository state (is repo, root, current branch, dirty) in one call.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>The repository snapshot; outside a repository only <see cref="GitRepositoryInfo.IsRepository"/> is false.</returns>
    Task<Outcome<GitRepositoryInfo>> GetRepositoryInfoAsync(string workspaceRoot);

    /// <summary>
    /// Detects the repository's main branch: <c>origin/HEAD</c> when set, else <c>main</c>, else <c>master</c>.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>The main branch name.</returns>
    Task<Outcome<string>> DetectMainBranchAsync(string workspaceRoot);

    /// <summary>
    /// Creates a branch from the current HEAD and switches to it.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="branchName">The new branch name.</param>
    /// <returns>Success or a <c>KA_GIT_FAILURE</c>.</returns>
    Task<Outcome> CreateAndSwitchBranchAsync(string workspaceRoot, string branchName);

    /// <summary>
    /// Stashes all local changes including untracked files.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>Success or a <c>KA_GIT_FAILURE</c>.</returns>
    Task<Outcome> StashAsync(string workspaceRoot);

    /// <summary>
    /// Stages every change (<c>git add -A</c>).
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>Success or a <c>KA_GIT_FAILURE</c>.</returns>
    Task<Outcome> StageAllAsync(string workspaceRoot);

    /// <summary>
    /// Commits staged changes with the given message.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="message">Short English commit message.</param>
    /// <returns>Success or a <c>KA_GIT_FAILURE</c>.</returns>
    Task<Outcome> CommitAsync(string workspaceRoot, string message);

    /// <summary>
    /// Pushes the branch to origin, setting upstream (<c>git push -u origin &lt;branch&gt;</c>). Never force.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="branchName">The branch to push.</param>
    /// <returns>Success or a <c>KA_GIT_FAILURE</c> with stderr in the message.</returns>
    Task<Outcome> PushBranchAsync(string workspaceRoot, string branchName);

    /// <summary>
    /// Lists the commits of the current branch (newest first).
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="maxCount">Maximum number of commits to return.</param>
    /// <returns>One-line-per-commit summaries ("&lt;hash&gt; &lt;subject&gt;").</returns>
    Task<Outcome<IReadOnlyList<string>>> ListCommitsAsync(string workspaceRoot, int maxCount);

    /// <summary>
    /// Adds <c>.agent/</c>, <c>tree-view-result.txt</c> and <c>code-dump-result.md</c> to
    /// <c>.git/info/exclude</c> when missing, so agent artifacts never show up as dirty state.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>Success or a <c>KA_GIT_FAILURE</c>.</returns>
    Task<Outcome> EnsureAgentPathsExcludedAsync(string workspaceRoot);
}
