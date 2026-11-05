using ImanSoftware.FileStorage;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Workspace;

/// <summary>
/// Probes the environment once at session start (in parallel, 3s timeout each) and produces
/// a compact block for the first chat message plus a one-line summary for reminders. The
/// per-tool states are also available structurally (for the fail-fast rule and the UIs) and
/// are cached in .agent/env.json for 24 h — with a per-tool 1 h freshness window and explicit
/// invalidation when reality disagrees with the cache.
/// </summary>
internal sealed class EnvironmentProbe : IEnvironmentTools
{
    private readonly IProcessRunner _runner;

    public EnvironmentProbe()
        : this(new ProcessRunner())
    {
    }

    internal EnvironmentProbe(IProcessRunner runner)
    {
        _runner = runner;
    }

    private sealed record EnvCache(string CreatedAtUtc, string Block, string Summary, Dictionary<string, ToolStatus>? Tools);

    /// <summary>
    /// Probes the environment unless a fresh (<24h) cache exists in .agent/env.json;
    /// forceRefresh (--refresh-env) bypasses the cache. Fresh results rewrite the cache.
    /// </summary>
    public async Task<Outcome<(string Block, string Summary, IReadOnlyDictionary<string, ToolStatus> Tools)>> ProbeCachedAsync(
        string workspaceRoot, bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var storage = ImanFileStorageFactory.CreateStorage();
        var cachePath = Path.Combine(workspaceRoot, ".agent", "env.json");

        if (!forceRefresh && storage.FileExists(cachePath))
        {
            var read = await storage.ReadAllTextAsync(cachePath);
            if (read.IsSuccess)
            {
                try
                {
                    var cached = System.Text.Json.JsonSerializer.Deserialize<EnvCache>(read.Data);
                    if (cached is not null &&
                        !string.IsNullOrWhiteSpace(cached.Block) &&
                        cached.Tools is { Count: > 0 } &&
                        DateTimeOffset.TryParse(cached.CreatedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) &&
                        DateTimeOffset.UtcNow - at < TimeSpan.FromHours(24))
                    {
                        return Outcome.Successful((cached.Block, cached.Summary ?? string.Empty,
                            (IReadOnlyDictionary<string, ToolStatus>)cached.Tools));
                    }
                }
                catch
                {
                    // A corrupt cache is as good as no cache.
                }
            }
        }

        var probe = await ProbeAsync(workspaceRoot, cancellationToken);
        if (probe.IsFailure)
            return probe;

        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new EnvCache(
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                probe.Data.Block,
                probe.Data.Summary,
                new Dictionary<string, ToolStatus>(probe.Data.Tools)));
            var mkdir = await storage.CreateDirectoryAsync(Path.GetDirectoryName(cachePath)!);
            if (mkdir.IsSuccess)
                await storage.WriteAllTextAsync(cachePath, payload);
        }
        catch
        {
            // Cache writes are best-effort; a missed cache only costs a re-probe.
        }

        return probe;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ToolStatus>> GetToolsCachedAsync(
        string workspaceRoot, bool forceRefresh, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeCachedAsync(workspaceRoot, forceRefresh, cancellationToken);
        return probe.IsSuccess ? probe.Data.Tools : new Dictionary<string, ToolStatus>();
    }

    /// <inheritdoc />
    public async Task<ToolStatus> GetToolStatusAsync(string workspaceRoot, string tool, CancellationToken cancellationToken = default)
    {
        var cache = await ReadCacheAsync(workspaceRoot);
        if (cache?.Tools is not null &&
            cache.Tools.TryGetValue(tool, out var cached) &&
            DateTimeOffset.TryParse(cached.CheckedAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var checkedAt) &&
            DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromHours(1))
        {
            return cached; // fresh enough (1 h) — no probe needed for the fail-fast rule
        }

        // The cache is older than 1 h (or does not know the tool): cheap single-tool re-probe.
        var refreshed = await ProbeToolAsync(workspaceRoot, tool, cancellationToken);
        await WriteCachedToolAsync(workspaceRoot, cache, refreshed, cancellationToken);
        return refreshed;
    }

    /// <inheritdoc />
    public async Task InvalidateToolAsync(string workspaceRoot, string tool, CancellationToken cancellationToken = default)
    {
        var refreshed = await ProbeToolAsync(workspaceRoot, tool, cancellationToken);
        await WriteCachedToolAsync(workspaceRoot, await ReadCacheAsync(workspaceRoot), refreshed, cancellationToken);
    }

    /// <summary>
    /// Probes OS, shell and tool versions in parallel (3s timeout each): git, dotnet, node,
    /// npm, and python resolved across python / py -3 / python3 with WindowsApps-store-alias
    /// detection. The block and the summary both carry explicit Available/Missing lists.
    /// </summary>
    /// <param name="workspaceRoot">The workspace (working directory for version probes).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The environment block, the one-line summary and the per-tool states.</returns>
    public async Task<Outcome<(string Block, string Summary, IReadOnlyDictionary<string, ToolStatus> Tools)>> ProbeAsync(
        string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var osTask = RunAsync("cmd", "/c ver", workspaceRoot, cancellationToken);
        var gitTask = RunAsync("git", "--version", workspaceRoot, cancellationToken);
        var dotnetTask = RunAsync("dotnet", "--version", workspaceRoot, cancellationToken);
        var nodeTask = RunAsync("node", "-v", workspaceRoot, cancellationToken);
        var npmTask = RunAsync("npm", "-v", workspaceRoot, cancellationToken);
        var pythonTask = RunAsync("python", "--version", workspaceRoot, cancellationToken);
        var pyTask = RunAsync("py", "-3 --version", workspaceRoot, cancellationToken);
        var python3Task = RunAsync("python3", "--version", workspaceRoot, cancellationToken);
        var wherePythonTask = RunAsync("cmd", "/c where python", workspaceRoot, cancellationToken);

        string?[] results;
        try
        {
            results = await Task.WhenAll(osTask, gitTask, dotnetTask, nodeTask, npmTask, pythonTask, pyTask, python3Task, wherePythonTask);
        }
        catch
        {
            // Per-probe failures already return null; this fires only for framework errors.
            results = [await osTask, await gitTask, await dotnetTask, await nodeTask, await npmTask, await pythonTask, await pyTask, await python3Task, await wherePythonTask];
        }

        string? os = results[0], gitV = results[1], dotnetV = results[2], nodeV = results[3], npmV = results[4],
            pythonV = results[5], pyV = results[6], python3V = results[7], wherePython = results[8];

        var tools = BuildToolStates(gitV, dotnetV, nodeV, npmV, pythonV, pyV, python3V, wherePython);
        var block = BuildBlock(os, tools, workspaceRoot);
        var summary = BuildSummary(tools);
        return Outcome.Successful((block, summary, (IReadOnlyDictionary<string, ToolStatus>)tools));
    }

    private static Dictionary<string, ToolStatus> BuildToolStates(
        string? gitV, string? dotnetV, string? nodeV, string? npmV,
        string? pythonV, string? pyV, string? python3V, string? wherePython)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var tools = new Dictionary<string, ToolStatus>(StringComparer.OrdinalIgnoreCase);

        void Add(string tool, string? version, string command)
        {
            tools[tool] = new ToolStatus(
                tool,
                string.IsNullOrWhiteSpace(version) ? ToolStates.Missing : ToolStates.Available,
                version?.Trim().Split('\n')[0].Trim() ?? string.Empty,
                command,
                now);
        }

        Add("git", gitV, "git");
        Add("dotnet", dotnetV, "dotnet");
        Add("node", nodeV, "node");
        Add("npm", npmV, "npm");
        Add("py", pyV, "py -3");
        Add("python3", python3V, "python3");

        // python resolves across python / py -3 / python3 — the first that wins is reported
        // WITH the command to use. A WindowsApps-only `where python` is a Store alias, never
        // a real install.
        var storeAliasOnly = pythonV is null &&
            wherePython is not null &&
            wherePython.Split('\n').Any(line => line.Trim().Length > 0 && line.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase));

        if (pythonV is not null)
            tools["python"] = new ToolStatus("python", ToolStates.Available, FirstLine(pythonV), "python", now);
        else if (pyV is not null)
            tools["python"] = new ToolStatus("python", ToolStates.Available, FirstLine(pyV), "py -3", now);
        else if (python3V is not null)
            tools["python"] = new ToolStatus("python", ToolStates.Available, FirstLine(python3V), "python3", now);
        else if (storeAliasOnly)
            tools["python"] = new ToolStatus("python", ToolStates.StoreAlias, "WindowsApps stub", string.Empty, now);
        else
            tools["python"] = new ToolStatus("python", ToolStates.Missing, string.Empty, string.Empty, now);

        return tools;
    }

    private static string BuildBlock(string? os, Dictionary<string, ToolStatus> tools, string workspaceRoot)
    {
        string Line(string tool)
        {
            var status = tools[tool];
            return status.State switch
            {
                ToolStates.Available when status.Command != tool => $"{tool}: {status.Version} (use: {status.Command})",
                ToolStates.Available => $"{tool}: {status.Version}",
                ToolStates.StoreAlias => $"{tool}: Store alias only (not a real install)",
                _ => $"{tool}: not installed",
            };
        }

        return string.Join("\n",
            "[ENVIRONMENT]",
            $"OS: {FirstLine(os)}",
            "Shell: cmd.exe",
            Line("git"),
            Line("dotnet"),
            Line("node"),
            Line("npm"),
            Line("python"),
            $"Available: {AvailableList(tools)}",
            $"Missing: {MissingList(tools)}",
            $"Workspace: {workspaceRoot}",
            "[/ENVIRONMENT]");
    }

    private static string BuildSummary(Dictionary<string, ToolStatus> tools)
        => $"Available: {AvailableList(tools)} | Missing: {MissingList(tools)}";

    private static string AvailableList(Dictionary<string, ToolStatus> tools)
    {
        var entries = tools.Values
            .Where(tool => tool.State == ToolStates.Available)
            .Select(tool => tool.Command != tool.Tool
                ? $"{tool.Tool} ({tool.Version}, use: {tool.Command})"
                : $"{tool.Tool} ({tool.Version})")
            .ToList();
        return entries.Count == 0 ? "none" : string.Join(", ", entries);
    }

    private static string MissingList(Dictionary<string, ToolStatus> tools)
    {
        var entries = tools.Values
            .Where(tool => tool.State is ToolStates.Missing or ToolStates.StoreAlias)
            .Select(tool => tool.State == ToolStates.StoreAlias ? $"{tool.Tool} (Store alias only)" : tool.Tool)
            .ToList();
        return entries.Count == 0 ? "none" : string.Join(", ", entries);
    }

    /// <summary>Probes ONE tool (python includes its py -3/python3 fallbacks and the store check).</summary>
    private async Task<ToolStatus> ProbeToolAsync(string workspaceRoot, string tool, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        if (tool is not ("python" or "py" or "python3"))
        {
            var (program, arguments) = VersionCommandOf(tool);
            var version = await RunAsync(program, arguments, workspaceRoot, cancellationToken);
            return new ToolStatus(tool,
                string.IsNullOrWhiteSpace(version) ? ToolStates.Missing : ToolStates.Available,
                version?.Trim().Split('\n')[0].Trim() ?? string.Empty,
                tool, now);
        }

        // The python family: the first working command wins; the Store alias is detected.
        var pythonV = await RunAsync("python", "--version", workspaceRoot, cancellationToken);
        var pyV = await RunAsync("py", "-3 --version", workspaceRoot, cancellationToken);
        var python3V = await RunAsync("python3", "--version", workspaceRoot, cancellationToken);
        var wherePython = await RunAsync("cmd", "/c where python", workspaceRoot, cancellationToken);
        var states = BuildToolStates(null, null, null, null, pythonV, pyV, python3V, wherePython);

        return tool switch
        {
            "py" => states["py"],
            "python3" => states["python3"],
            _ => states["python"],
        };
    }

    private static (string Program, string Arguments) VersionCommandOf(string tool) => tool switch
    {
        "py" => ("py", "-3 --version"),
        _ => (tool, "--version"),
    };

    private async Task<EnvCache?> ReadCacheAsync(string workspaceRoot)
    {
        var storage = ImanFileStorageFactory.CreateStorage();
        var cachePath = Path.Combine(workspaceRoot, ".agent", "env.json");
        if (!storage.FileExists(cachePath))
            return null;

        var read = await storage.ReadAllTextAsync(cachePath);
        if (!read.IsSuccess)
            return null;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<EnvCache>(read.Data);
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteCachedToolAsync(string workspaceRoot, EnvCache? cache, ToolStatus refreshed, CancellationToken cancellationToken)
    {
        try
        {
            var storage = ImanFileStorageFactory.CreateStorage();
            var cachePath = Path.Combine(workspaceRoot, ".agent", "env.json");
            var tools = cache?.Tools is { Count: > 0 }
                ? new Dictionary<string, ToolStatus>(cache.Tools, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ToolStatus>(StringComparer.OrdinalIgnoreCase);
            tools[refreshed.Tool] = refreshed;

            var payload = System.Text.Json.JsonSerializer.Serialize(new EnvCache(
                cache?.CreatedAtUtc ?? DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                cache?.Block ?? string.Empty,
                cache?.Summary ?? string.Empty,
                tools));
            var mkdir = await storage.CreateDirectoryAsync(Path.GetDirectoryName(cachePath)!);
            if (mkdir.IsSuccess)
                await storage.WriteAllTextAsync(cachePath, payload);
        }
        catch
        {
            // Cache writes are best-effort.
        }
    }

    private async Task<string?> RunAsync(string program, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runner.RunAsync(program, arguments, workingDirectory, timeoutSeconds: 3, cancellationToken);
            var output = (result.Output + " " + result.Error).Trim();
            return result.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string FirstLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "not installed" : text.Trim().Split('\n')[0].Trim();
}
