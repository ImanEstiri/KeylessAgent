using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Models.Git;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Tests.Fakes;

/// <summary>
/// In-memory git service: the branch and dirty state are set by the test; every call is recorded.
/// </summary>
internal sealed class FakeGitService : IGitService
{
    public string CurrentBranch { get; set; } = "agent/test";

    public bool IsRepository { get; set; } = true;

    public bool Dirty { get; set; }

    public List<string> Calls { get; } = [];

    public Func<string, Outcome>? PushResult { get; set; }

    public Task<Outcome<GitRepositoryInfo>> GetRepositoryInfoAsync(string workspaceRoot)
    {
        Calls.Add("info");
        return Task.FromResult(Outcome.Successful(new GitRepositoryInfo
        {
            IsRepository = IsRepository,
            RepositoryRoot = IsRepository ? workspaceRoot : null,
            CurrentBranch = CurrentBranch,
            IsDirty = Dirty,
        }));
    }

    public Task<Outcome<string>> DetectMainBranchAsync(string workspaceRoot)
    {
        Calls.Add("detect-main");
        return Task.FromResult(Outcome.Successful("main"));
    }

    public Task<Outcome> CreateAndSwitchBranchAsync(string workspaceRoot, string branchName)
    {
        Calls.Add($"checkout-b:{branchName}");
        CurrentBranch = branchName;
        return Task.FromResult(Outcome.Successful());
    }

    public Task<Outcome> StashAsync(string workspaceRoot)
    {
        Calls.Add("stash");
        return Task.FromResult(Outcome.Successful());
    }

    public Task<Outcome> StageAllAsync(string workspaceRoot)
    {
        Calls.Add("add-all");
        return Task.FromResult(Outcome.Successful());
    }

    public Task<Outcome> CommitAsync(string workspaceRoot, string message)
    {
        Calls.Add($"commit:{message}");
        return Task.FromResult(Outcome.Successful());
    }

    public Task<Outcome> PushBranchAsync(string workspaceRoot, string branchName)
    {
        Calls.Add($"push:{branchName}");
        return Task.FromResult(PushResult?.Invoke(branchName) ?? Outcome.Successful());
    }

    public Task<Outcome<IReadOnlyList<string>>> ListCommitsAsync(string workspaceRoot, int maxCount)
    {
        Calls.Add("log");
        return Task.FromResult(Outcome.Successful<IReadOnlyList<string>>(["abc123 test commit"]));
    }

    public Task<Outcome> EnsureAgentPathsExcludedAsync(string workspaceRoot)
    {
        Calls.Add("exclude");
        return Task.FromResult(Outcome.Successful());
    }
}

/// <summary>
/// A predictable clock for tests: Now never moves unless the test says so.
/// </summary>
internal sealed class FakeDateTimeProvider : ImanSoftware.DateTimeProvider.IDateTimeProvider
{
    public DateTime Now { get; set; } = new(2026, 1, 1, 9, 30, 0);

    public DateOnly Today => DateOnly.FromDateTime(Now);

    public TimeOnly CurrentTimeOfDay => TimeOnly.FromDateTime(Now);
}
