using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Git;

/// <summary>
/// The in-code protected-branch rule. Patterns are matched case-insensitively with <c>*</c> wildcard support.
/// </summary>
internal sealed class BranchGuard : IBranchGuard
{
    private readonly IGitService _git;

    public BranchGuard()
        : this(new GitService())
    {
    }

    internal BranchGuard(IGitService git)
    {
        _git = git;
    }

    /// <inheritdoc />
    public bool IsProtected(string branchName, GitOptions options)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            return true;

        return options.ProtectedBranchPatterns.Any(pattern =>
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, branchName, ignoreCase: true));
    }

    /// <inheritdoc />
    public async Task<Outcome> EnsureModifiableAsync(string workspaceRoot, GitOptions options)
    {
        var infoOutcome = await _git.GetRepositoryInfoAsync(workspaceRoot);
        if (infoOutcome.IsFailure)
            return infoOutcome;

        var info = infoOutcome.Data;
        if (!info.IsRepository)
        {
            return Outcome.Failure(new OutcomeError(
                "The workspace is not a git repository.", ErrorCodes.NotAGitRepository, OutcomeErrorType.Problem));
        }

        if (IsProtected(info.CurrentBranch, options))
        {
            return Outcome.Failure(new OutcomeError(
                $"Branch \"{info.CurrentBranch}\" is protected; refusing to modify it. Switch to an agent branch first.",
                ErrorCodes.BranchProtected, OutcomeErrorType.Problem));
        }

        return Outcome.Successful();
    }
}
