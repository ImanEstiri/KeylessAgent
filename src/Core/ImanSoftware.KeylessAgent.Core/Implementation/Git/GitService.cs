using ImanSoftware.Extensions;
using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Git;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Git;

/// <summary>
/// Git operations as short-lived <c>git</c> processes run inside the workspace.
/// Never force-pushes, never resets shared branches, never deletes branches, never merges or rebases.
/// </summary>
internal sealed class GitService : IGitService
{
    private readonly ProcessRunner _runner;

    public GitService()
        : this(new ProcessRunner())
    {
    }

    internal GitService(ProcessRunner runner)
    {
        _runner = runner;
    }

    /// <inheritdoc />
    public async Task<Outcome<GitRepositoryInfo>> GetRepositoryInfoAsync(string workspaceRoot)
    {
        var inside = await RunAsync(workspaceRoot, "rev-parse --is-inside-work-tree");
        if (inside.IsFailure)
            return Outcome.Failure<GitRepositoryInfo>(inside.Error);

        if (!inside.Data.Output.Trim().StartsWith("true", StringComparison.OrdinalIgnoreCase))
        {
            return Outcome.Successful(new GitRepositoryInfo { IsRepository = false });
        }

        var root = await RunAsync(workspaceRoot, "rev-parse --show-toplevel");
        if (root.IsFailure)
            return Outcome.Failure<GitRepositoryInfo>(root.Error);

        // `rev-parse --abbrev-ref HEAD` returns "HEAD" on an UNBORN branch (no commits yet);
        // `branch --show-current` returns the real name there, so prefer it.
        var branch = await RunAsync(workspaceRoot, "branch --show-current");
        if (branch.IsFailure || branch.Data.Output.Trim().Length == 0)
            branch = await RunAsync(workspaceRoot, "rev-parse --abbrev-ref HEAD");
        if (branch.IsFailure)
            return Outcome.Failure<GitRepositoryInfo>(branch.Error);

        var status = await RunAsync(workspaceRoot, "status --porcelain");
        if (status.IsFailure)
            return Outcome.Failure<GitRepositoryInfo>(status.Error);

        return Outcome.Successful(new GitRepositoryInfo
        {
            IsRepository = true,
            RepositoryRoot = root.Data.Output.Trim(),
            CurrentBranch = branch.Data.Output.Trim(),
            IsDirty = status.Data.Output.Trim().Length > 0,
        });
    }

    /// <inheritdoc />
    public async Task<Outcome<string>> DetectMainBranchAsync(string workspaceRoot)
    {
        var head = await RunAsync(workspaceRoot, "symbolic-ref refs/remotes/origin/HEAD --short");
        if (head.IsSuccess)
        {
            var name = head.Data.Output.Trim();
            if (name.StartsWith("origin/", StringComparison.OrdinalIgnoreCase))
                name = name["origin/".Length..];
            if (name.Length > 0)
                return Outcome.Successful(name);
        }

        foreach (var candidate in (string[])["main", "master"])
        {
            var exists = await RunAsync(workspaceRoot, $"rev-parse --verify --quiet origin/{candidate}");
            if (exists.IsSuccess && exists.Data.ExitCode == 0)
                return Outcome.Successful(candidate);
        }

        return Outcome.Successful("main");
    }

    /// <inheritdoc />
    public Task<Outcome> CreateAndSwitchBranchAsync(string workspaceRoot, string branchName)
        => RunToOutcomeAsync(workspaceRoot, $"checkout -b {Quote(branchName)}");

    /// <inheritdoc />
    public Task<Outcome> StashAsync(string workspaceRoot)
        => RunToOutcomeAsync(workspaceRoot, "stash push -u -m \"keyless-agent auto stash\"");

    /// <inheritdoc />
    public Task<Outcome> StageAllAsync(string workspaceRoot)
        => RunToOutcomeAsync(workspaceRoot, "add -A");

    /// <inheritdoc />
    public Task<Outcome> CommitAsync(string workspaceRoot, string message)
        => RunToOutcomeAsync(workspaceRoot, $"commit -m {Quote(message)}");

    /// <inheritdoc />
    public Task<Outcome> PushBranchAsync(string workspaceRoot, string branchName)
        => RunToOutcomeAsync(workspaceRoot, $"push -u origin {Quote(branchName)}");

    /// <inheritdoc />
    public async Task<Outcome<IReadOnlyList<string>>> ListCommitsAsync(string workspaceRoot, int maxCount)
    {
        var log = await RunAsync(workspaceRoot, $"log --oneline -n {maxCount}");
        if (log.IsFailure)
            return Outcome.Failure<IReadOnlyList<string>>(log.Error);

        var lines = log.Data.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return Outcome.Successful((IReadOnlyList<string>)lines);
    }

    /// <inheritdoc />
    public async Task<Outcome<bool>> IsInstalledAsync()
    {
        var result = await RunAsync(Directory.GetCurrentDirectory(), "--version");
        return result.IsSuccess && result.Data.ExitCode == 0
            ? Outcome.Successful(true)
            : Outcome.Successful(false);
    }

    /// <inheritdoc />
    public async Task<Outcome> InitRepositoryAsync(string workspaceRoot)
    {
        var init = await RunAsync(workspaceRoot, "init -b main");
        if (init.IsFailure || init.Data.ExitCode != 0)
        {
            // Old git without -b: init then rename the branch.
            var plain = await RunAsync(workspaceRoot, "init");
            if (plain.IsFailure || plain.Data.ExitCode != 0)
                return plain;

            var rename = await RunAsync(workspaceRoot, "branch -M main");
            if (rename.IsFailure || rename.Data.ExitCode != 0)
                return rename;
        }

        return Outcome.Successful();
    }

    /// <inheritdoc />
    public async Task<Outcome> EnsureDotnetGitignoreAsync(string workspaceRoot)
    {
        var gitignorePath = Path.Combine(workspaceRoot, ".gitignore");
        if (File.Exists(gitignorePath))
            return Outcome.Successful();

        var hasProject = Directory.EnumerateFiles(workspaceRoot, "*.sln", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(workspaceRoot, "*.slnx", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories).Any();
        if (!hasProject)
            return Outcome.Successful();

        // `dotnet new gitignore` is a dotnet-template command, not a git command.
        var template = await _runner.RunAsync("dotnet", "new gitignore --force", workspaceRoot, timeoutSeconds: 120);
        if (template.ExitCode == 0 && File.Exists(gitignorePath))
            return Outcome.Successful();

        var content = string.Join(Environment.NewLine,
            "bin/", "obj/", ".vs/", "*.user", ".idea/", "artifacts/", "TestResults/") + Environment.NewLine;
        var write = await ImanFileStorageFactory.CreateStorage().WriteAllTextAsync(gitignorePath, content);
        return write.IsFailure ? write : Outcome.Successful();
    }

    /// <inheritdoc />
    public async Task<Outcome> CreateInitialCommitAsync(string workspaceRoot)
    {
        var stage = await RunAsync(workspaceRoot, "add -A");
        if (stage.IsFailure || stage.Data.ExitCode != 0)
            return stage;

        var identity = await RunAsync(workspaceRoot, "config user.name");
        var hasName = identity.IsSuccess && identity.Data.ExitCode == 0 && identity.Data.Output.Trim().Length > 0;
        var email = await RunAsync(workspaceRoot, "config user.email");
        var hasEmail = email.IsSuccess && email.Data.ExitCode == 0 && email.Data.Output.Trim().Length > 0;

        var configPrefix = hasName && hasEmail
            ? string.Empty
            : "-c user.name=\"KeylessAgent\" -c user.email=\"keyless@localhost\" ";

        return await RunToOutcomeAsync(workspaceRoot,
            $"{configPrefix}commit -m {Quote("Initial commit (created by KeylessAgent)")}");
    }

    /// <inheritdoc />
    public async Task<Outcome<bool>> HasOriginRemoteAsync(string workspaceRoot)
    {
        var remotes = await RunAsync(workspaceRoot, "remote");
        if (remotes.IsFailure)
            return Outcome.Failure<bool>(remotes.Error);

        var hasOrigin = remotes.Data.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(r => r.Equals("origin", StringComparison.OrdinalIgnoreCase));
        return Outcome.Successful(hasOrigin);
    }

    /// <inheritdoc />
    public async Task<Outcome> EnsureAgentPathsExcludedAsync(string workspaceRoot)
    {
        var excludePath = Path.Combine(workspaceRoot, ".git", "info", "exclude");
        var requiredEntries = new[] { ".agent/", "tree-view-result.txt", "code-dump-result.md" };

        var storage = ImanFileStorageFactory.CreateStorage();
        string content = string.Empty;
        if (storage.FileExists(excludePath))
        {
            var read = await storage.ReadAllTextAsync(excludePath);
            if (read.IsFailure)
                return Outcome.Failure(new OutcomeError(
                    $"Cannot read {excludePath}: {read.Error.Message}", ErrorCodes.GitFailure, OutcomeErrorType.Failure));
            content = read.Data;
        }

        var lines = content.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var changed = false;
        foreach (var entry in requiredEntries)
        {
            if (!lines.Any(l => l.TrimEnd('\r') == entry))
            {
                lines.Add(entry);
                changed = true;
            }
        }

        if (!changed)
            return Outcome.Successful();

        var directoryOutcome = await storage.CreateDirectoryAsync(Path.GetDirectoryName(excludePath)!);
        if (directoryOutcome.IsFailure)
            return directoryOutcome;

        var write = await storage.WriteAllTextAsync(excludePath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        if (write.IsFailure)
            return Outcome.Failure(new OutcomeError(
                $"Cannot write {excludePath}: {write.Error.Message}", ErrorCodes.GitFailure, OutcomeErrorType.Failure));

        return Outcome.Successful();
    }

    private async Task<Outcome<ProcessResult>> RunAsync(string workspaceRoot, string arguments)
    {
        try
        {
            var result = await _runner.RunAsync("git", arguments, workspaceRoot, timeoutSeconds: 60);
            if (result.TimedOut)
            {
                return Outcome.Failure<ProcessResult>(new OutcomeError(
                    $"git {arguments.Split(' ')[0]} timed out.", ErrorCodes.Timeout, OutcomeErrorType.Failure));
            }

            return Outcome.Successful(result);
        }
        catch (Exception ex)
        {
            return Outcome.Failure<ProcessResult>(new OutcomeError(ex));
        }
    }

    private async Task<Outcome> RunToOutcomeAsync(string workspaceRoot, string arguments)
    {
        var result = await RunAsync(workspaceRoot, arguments);
        if (result.IsFailure)
            return result;

        var process = result.Data;
        if (process.ExitCode != 0)
        {
            return Outcome.Failure(new OutcomeError(
                $"git {arguments.Split(' ')[0]} failed (exit {process.ExitCode}): {CombineOutput(process).Truncate(400)}",
                ErrorCodes.GitFailure, OutcomeErrorType.Failure));
        }

        return Outcome.Successful();
    }

    private static string CombineOutput(ProcessResult result)
    {
        var parts = new[] { result.Output.Trim(), result.Error.Trim() }.Where(p => p.Length > 0);
        return string.Join(" | ", parts);
    }

    private static string Quote(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;
}
