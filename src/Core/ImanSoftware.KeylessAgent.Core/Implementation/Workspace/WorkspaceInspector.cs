using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Workspace;

/// <summary>
/// Probes the workspace capabilities once per instance and caches the result, so no git
/// decision re-runs `git --version` or the repository checks.
/// </summary>
internal sealed class WorkspaceInspector : IWorkspaceInspector
{
    private readonly IGitService _git;
    private WorkspaceCapabilities? _cached;

    public WorkspaceInspector()
        : this(new GitService())
    {
    }

    internal WorkspaceInspector(IGitService git)
    {
        _git = git;
    }

    /// <inheritdoc />
    public async Task<Outcome<WorkspaceCapabilities>> InspectAsync(
        string workspaceRoot, GitOptions options, CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
            return Outcome.Successful(_cached);

        var installed = await _git.IsInstalledAsync();
        var gitInstalled = installed.IsSuccess && installed.Data;

        if (!gitInstalled)
        {
            _cached = new WorkspaceCapabilities(
                GitInstalled: false, IsRepository: false, CurrentBranch: string.Empty, IsAgentBranch: false);
            return Outcome.Successful(_cached);
        }

        var info = await _git.GetRepositoryInfoAsync(workspaceRoot);
        var isRepository = info.IsSuccess && info.Data.IsRepository;
        var currentBranch = isRepository ? info.Data.CurrentBranch : string.Empty;
        var isAgentBranch = isRepository &&
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                options.BranchPrefix + "*", currentBranch, ignoreCase: true);

        _cached = new WorkspaceCapabilities(gitInstalled, isRepository, currentBranch, isAgentBranch);
        return Outcome.Successful(_cached);
    }
}

/// <summary>
/// Change tracking with and without git: with git, `git status --porcelain` snapshots are
/// compared; without git, a lightweight path/size/mtime index of the workspace (excluding the
/// configured ignore patterns) is used.
/// </summary>
internal sealed class ChangeTracker : IChangeTracker
{
    private readonly IGitService _git;
    private readonly IFileStorage _storage;

    public ChangeTracker()
        : this(new GitService(), ImanFileStorageFactory.CreateStorage())
    {
    }

    internal ChangeTracker(IGitService git, IFileStorage storage)
    {
        _git = git;
        _storage = storage;
    }

    private static readonly string[] IgnoredFolders =
        ["bin", "obj", ".git", ".agent", "node_modules", "artifacts", ".vs", ".idea", "TestResults"];

    /// <inheritdoc />
    public async Task<Outcome<object>> CaptureAsync(
        string workspaceRoot, bool gitAvailable, CancellationToken cancellationToken = default)
    {
        if (gitAvailable)
        {
            var status = await _git.RunStatusPorcelainAsync(workspaceRoot);
            return status.IsFailure
                ? status
                : Outcome.Successful<object>(status.Data.ToList());
        }

        var index = new Dictionary<string, (long Size, DateTime Mtime)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateWorkspaceFiles(workspaceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                index[Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/')] = (info.Length, info.LastWriteTimeUtc);
            }
            catch
            {
                // A file that cannot be stat-ed is skipped; the diff reports it only if it appears.
            }
        }

        return Outcome.Successful<object>(index);
    }

    /// <inheritdoc />
    public async Task<Outcome<IReadOnlyList<ChangedPath>>> DiffAsync(
        string workspaceRoot, object baseline, bool gitAvailable, CancellationToken cancellationToken = default)
    {
        if (gitAvailable && baseline is List<string> previousPorcelain)
        {
            var now = await _git.RunStatusPorcelainAsync(workspaceRoot);
            if (now.IsFailure)
                return Outcome.Failure<IReadOnlyList<ChangedPath>>(now.Error);

            var before = ParsePorcelain(previousPorcelain);
            var after = ParsePorcelain(now.Data.ToList());

            var changed = after.Where(a => !before.TryGetValue(a.Key, out var b) || b != a.Value)
                .Select(a => new ChangedPath(a.Key, a.Value))
                .ToList();
            var deleted = before.Keys.Where(k => !after.ContainsKey(k))
                .Select(k => new ChangedPath(k, "deleted"));
            return Outcome.Successful((IReadOnlyList<ChangedPath>)changed.Concat(deleted).ToList());
        }

        if (baseline is Dictionary<string, (long Size, DateTime Mtime)> previousIndex)
        {
            var currentIndex = new Dictionary<string, (long Size, DateTime Mtime)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateWorkspaceFiles(workspaceRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    currentIndex[Path.GetRelativePath(workspaceRoot, file).Replace('\\', '/')] = (info.Length, info.LastWriteTimeUtc);
                }
                catch
                {
                    // Unreadable now → treated as deleted below (not present in the index).
                }
            }

            var result = new List<ChangedPath>();
            foreach (var (path, current) in currentIndex)
            {
                if (!previousIndex.TryGetValue(path, out var before))
                    result.Add(new ChangedPath(path, "added"));
                else if (before.Size != current.Size || before.Mtime != current.Mtime)
                    result.Add(new ChangedPath(path, "modified"));
            }

            result.AddRange(previousIndex.Keys
                .Where(path => !currentIndex.ContainsKey(path))
                .Select(path => new ChangedPath(path, "deleted")));

            return Outcome.Successful((IReadOnlyList<ChangedPath>)result
                .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
                .ToList());
        }

        return Outcome.Failure<IReadOnlyList<ChangedPath>>(new OutcomeError(
            "Unknown baseline type.", "KA_INTERNAL", OutcomeErrorType.Problem));
    }

    private static Dictionary<string, string> ParsePorcelain(List<string> lines)
    {
        // "XY path" / "XY \"path\"" / "R  old -> new" (we keep the new path for renames).
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.Length < 4)
                continue;

            var kind = line[..2].Trim();
            var path = line[3..].Trim('"');
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                path = path[(arrow + 4)..];

            result[path.Replace('\\', '/')] = kind switch
            {
                "A" => "added",
                "D" => "deleted",
                "R" => "renamed",
                _ => "modified",
            };
        }

        return result;
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles(string workspaceRoot)
    {
        var stack = new Stack<string>();
        stack.Push(workspaceRoot);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files = [];
            string[] directories = [];
            try
            {
                files = Directory.EnumerateFiles(current).ToArray();
                directories = Directory.EnumerateDirectories(current).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (IgnoredFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                    continue;

                stack.Push(directory);
            }
        }
    }
}
