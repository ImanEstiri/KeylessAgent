using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Enforces the protected-branch rule in code, not only in the prompt:
/// no file change, commit or push ever happens on <c>main</c>, <c>master</c> or <c>release*</c>.
/// </summary>
public interface IBranchGuard : IService
{
    /// <summary>
    /// Checks whether the given branch name matches any protected pattern.
    /// </summary>
    /// <param name="branchName">The branch to test.</param>
    /// <param name="options">The configured git options (protected patterns).</param>
    /// <returns>True when the branch is protected.</returns>
    bool IsProtected(string branchName, GitOptions options);

    /// <summary>
    /// Verifies the current branch of the workspace may be modified.
    /// Runs before every file-changing command and before commit/push.
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <param name="options">The configured git options (protected patterns).</param>
    /// <returns>Success, or a <c>KA_BRANCH_PROTECTED</c> failure naming the branch.</returns>
    Task<Outcome> EnsureModifiableAsync(string workspaceRoot, GitOptions options);
}
