using System.IO;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Workspace;

/// <summary>
/// The environment probe: python resolved across python / py -3 / python3 with WindowsApps
/// store-alias detection, explicit Available/Missing lists, the 24 h cache with per-tool
/// 1 h freshness and explicit invalidation, and the one-line Tools status for the UIs.
/// </summary>
public class EnvironmentProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ka-envprobe-" + Guid.CreateVersion7().ToString("N"));

    public EnvironmentProbeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private sealed class FakeRunner(Dictionary<string, string> outputs) : IProcessRunner
    {
        public int Calls { get; private set; }

        public Dictionary<string, string> Outputs { get; } = outputs;

        public Task<ProcessResult> RunAsync(
            string fileName, string arguments, string workingDirectory,
            int timeoutSeconds, CancellationToken cancellationToken = default)
        {
            Calls++;
            var key = $"{fileName} {arguments}".Trim();
            return Task.FromResult(Outputs.TryGetValue(key, out var output)
                ? new ProcessResult(0, output, string.Empty, 0.01, TimedOut: false)
                : new ProcessResult(9009, string.Empty,
                    $"'{fileName}' is not recognized as an internal or external command", 0.01, TimedOut: false));
        }
    }

    private static Dictionary<string, string> BaseOutputs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd /c ver"] = "Microsoft Windows [Version 10.0.19045.4046]",
        ["git --version"] = "git version 2.45.0",
        ["dotnet --version"] = "10.0.100",
        ["node -v"] = string.Empty,
        ["npm -v"] = string.Empty,
        ["python --version"] = string.Empty,
        ["py -3 --version"] = string.Empty,
        ["python3 --version"] = string.Empty,
        ["cmd /c where python"] = string.Empty,
    };

    [Fact]
    public async Task Probe_PythonViaPyLauncher_ReportsCommandAndExplicitLists()
    {
        var outputs = BaseOutputs();
        outputs["py -3 --version"] = "Python 3.12.1";
        outputs["cmd /c where python"] = "C:\\Users\\x\\AppData\\Local\\Microsoft\\WindowsApps\\python.exe";
        var probe = new EnvironmentProbe(new FakeRunner(outputs));

        var outcome = await probe.ProbeAsync(_root);

        outcome.IsSuccess.Should().BeTrue();
        var python = outcome.Data.Tools["python"];
        python.State.Should().Be(ToolStates.Available);
        python.Command.Should().Be("py -3", "the launcher is the first command that works");
        python.Version.Should().Be("Python 3.12.1");

        outcome.Data.Block.Should().Contain("python: Python 3.12.1 (use: py -3)");
        outcome.Data.Block.Should().Contain("Available: git (git version 2.45.0), dotnet (10.0.100), py (Python 3.12.1, use: py -3), python (Python 3.12.1, use: py -3)");
        outcome.Data.Block.Should().Contain("Missing: node, npm");
        outcome.Data.Summary.Should().Contain("Available: git (git version 2.45.0), dotnet (10.0.100), py (Python 3.12.1, use: py -3), python (Python 3.12.1, use: py -3)");
        outcome.Data.Summary.Should().Contain("Missing: node, npm");
    }

    [Fact]
    public async Task Probe_StoreAliasOnly_IsMarkedAndListedAsMissing()
    {
        var outputs = BaseOutputs();
        outputs["cmd /c where python"] = "C:\\Users\\x\\AppData\\Local\\Microsoft\\WindowsApps\\python.exe";
        var probe = new EnvironmentProbe(new FakeRunner(outputs));

        var outcome = await probe.ProbeAsync(_root);

        var python = outcome.Data.Tools["python"];
        python.State.Should().Be(ToolStates.StoreAlias, "`where python` resolves only to the WindowsApps stub");
        outcome.Data.Block.Should().Contain("python: Store alias only (not a real install)");
        outcome.Data.Summary.Should().Contain("python (Store alias only)");
    }

    [Fact]
    public async Task GetToolStatus_FreshCacheIsServedWithoutProbing()
    {
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var cache = System.Text.Json.JsonSerializer.Serialize(new
        {
            CreatedAtUtc = now,
            Block = "[ENVIRONMENT][/ENVIRONMENT]",
            Summary = string.Empty,
            Tools = new Dictionary<string, ToolStatus>(StringComparer.Ordinal)
            {
                ["python"] = new("python", ToolStates.Available, "Python 3.12.1", "python", now),
            },
        });
        Directory.CreateDirectory(Path.Combine(_root, ".agent"));
        File.WriteAllText(Path.Combine(_root, ".agent", "env.json"), cache);

        var runner = new FakeRunner(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var probe = new EnvironmentProbe(runner);

        var status = await probe.GetToolStatusAsync(_root, "python");

        status.State.Should().Be(ToolStates.Available);
        runner.Calls.Should().Be(0, "a cache entry checked within the last hour is served without a probe");
    }

    [Fact]
    public async Task GetToolStatus_ReprobesWhenTheEntryIsOlderThan1Hour()
    {
        var stale = DateTimeOffset.UtcNow.AddHours(-2).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var cache = System.Text.Json.JsonSerializer.Serialize(new
        {
            CreatedAtUtc = stale,
            Block = "[ENVIRONMENT][/ENVIRONMENT]",
            Summary = string.Empty,
            Tools = new Dictionary<string, ToolStatus>(StringComparer.Ordinal)
            {
                ["dotnet"] = new("dotnet", ToolStates.Missing, string.Empty, string.Empty, stale),
            },
        });
        Directory.CreateDirectory(Path.Combine(_root, ".agent"));
        File.WriteAllText(Path.Combine(_root, ".agent", "env.json"), cache);

        var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["dotnet --version"] = "10.0.100" };
        var probe = new EnvironmentProbe(new FakeRunner(outputs));

        var status = await probe.GetToolStatusAsync(_root, "dotnet");

        status.State.Should().Be(ToolStates.Available, "a cache entry older than 1 h is re-probed (cheap)");
        status.Version.Should().Be("10.0.100");
    }

    [Fact]
    public async Task InvalidateTool_UpdatesTheCache_BothWays()
    {
        var now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        var cache = System.Text.Json.JsonSerializer.Serialize(new
        {
            CreatedAtUtc = now,
            Block = "[ENVIRONMENT][/ENVIRONMENT]",
            Summary = string.Empty,
            Tools = new Dictionary<string, ToolStatus>(StringComparer.Ordinal)
            {
                ["python"] = new("python", ToolStates.Available, "Python 3.11.0", "python", now),
            },
        });
        Directory.CreateDirectory(Path.Combine(_root, ".agent"));
        File.WriteAllText(Path.Combine(_root, ".agent", "env.json"), cache);

        // Cache says installed, reality says missing: the invalidation records the truth.
        var missingRunner = new FakeRunner(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var probe = new EnvironmentProbe(missingRunner);
        await probe.InvalidateToolAsync(_root, "python");
        var status = await probe.GetToolStatusAsync(_root, "python");
        status.State.Should().Be(ToolStates.Missing);
        File.ReadAllText(Path.Combine(_root, ".agent", "env.json"))
            .Should().Contain("\"missing\"", "the cache must not keep claiming the tool is installed");

        // The reverse: the cache said missing, reality says present.
        var presentRunner = new FakeRunner(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["python --version"] = "Python 3.13.0",
        });
        var presentProbe = new EnvironmentProbe(presentRunner);
        await presentProbe.InvalidateToolAsync(_root, "python");
        var present = await presentProbe.GetToolStatusAsync(_root, "python");
        present.State.Should().Be(ToolStates.Available, "a cache miss is corrected too");
        File.ReadAllText(Path.Combine(_root, ".agent", "env.json"))
            .Should().Contain("Python 3.13.0");
    }

    [Fact]
    public void FormatToolsLine_ListsStatusWithDownloadPages()
    {
        var now = string.Empty;
        var tools = new Dictionary<string, ToolStatus>(StringComparer.Ordinal)
        {
            ["git"] = new("git", ToolStates.Available, "2.45.0", "git", now),
            ["dotnet"] = new("dotnet", ToolStates.Missing, string.Empty, string.Empty, now),
            ["python"] = new("python", ToolStates.Missing, string.Empty, string.Empty, now),
            ["node"] = new("node", ToolStates.Missing, string.Empty, string.Empty, now),
            ["npm"] = new("npm", ToolStates.Missing, string.Empty, string.Empty, now),
        };

        var line = IEnvironmentTools.FormatToolsLine(tools);

        line.Should().Be("Tools: git OK, dotnet MISSING (https://dotnet.microsoft.com/download), " +
            "python MISSING (https://www.python.org/downloads/), " +
            "node MISSING (https://nodejs.org), npm MISSING (https://nodejs.org)");
    }

    [Fact]
    public async Task GetToolsCached_MissingWorkspaceCache_ProbesAndReturnsTheTools()
    {
        var outputs = BaseOutputs();
        outputs["git --version"] = "git version 2.45.0";
        var probe = new EnvironmentProbe(new FakeRunner(outputs));

        var tools = await probe.GetToolsCachedAsync(_root, forceRefresh: false);

        tools["git"].State.Should().Be(ToolStates.Available);
        tools["python"].State.Should().Be(ToolStates.Missing);
    }
}
