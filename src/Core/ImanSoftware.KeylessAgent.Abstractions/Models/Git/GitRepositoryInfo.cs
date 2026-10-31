namespace ImanSoftware.KeylessAgent.Abstractions.Models.Git;

/// <summary>
/// A snapshot of the git state the agent checks before starting a session.
/// </summary>
public sealed record GitRepositoryInfo
{
    /// <summary>
    /// Whether the workspace is inside a git work tree.
    /// </summary>
    public bool IsRepository { get; init; }

    /// <summary>
    /// Absolute path of the repository root, or null outside a repository.
    /// </summary>
    public string? RepositoryRoot { get; init; }

    /// <summary>
    /// The currently checked-out branch, or "DETACHED" for a detached HEAD.
    /// </summary>
    public string CurrentBranch { get; init; } = string.Empty;

    /// <summary>
    /// Whether the working tree has uncommitted changes.
    /// </summary>
    public bool IsDirty { get; init; }
}
