namespace ImanSoftware.KeylessAgent.Core.Implementation.Workspace;

/// <summary>The well-known states of a probed tool.</summary>
public static class ToolStates
{
    /// <summary>The tool answered its version probe.</summary>
    public const string Available = "available";

    /// <summary>The command resolves only to a WindowsApps Store stub (not a real install).</summary>
    public const string StoreAlias = "store-alias";

    /// <summary>The tool did not answer its version probe.</summary>
    public const string Missing = "missing";
}

/// <summary>The probed state of one tool.</summary>
/// <param name="Tool">Canonical tool name (git, dotnet, node, npm, python, py, python3).</param>
/// <param name="State">Available, store-alias or missing (see <see cref="ToolStates"/>).</param>
/// <param name="Version">First line of the version output (empty when missing).</param>
/// <param name="Command">The command that works for this tool ("py -3" when the python launcher won).</param>
/// <param name="CheckedAtUtc">When this status was probed (drives the per-tool 1 h freshness rule).</param>
public sealed record ToolStatus(string Tool, string State, string Version, string Command, string CheckedAtUtc);

/// <summary>
/// Structured access to the probed environment tools: the command executor uses it to fail
/// fast on missing tools, the UIs use it for the user-facing "Tools:" line. Implementations
/// cache (24 h) and support per-tool re-probes (1 h freshness) plus explicit invalidation.
/// </summary>
public interface IEnvironmentTools
{
    /// <summary>Returns every tool status (cached unless <paramref name="forceRefresh"/>).</summary>
    Task<IReadOnlyDictionary<string, ToolStatus>> GetToolsCachedAsync(
        string workspaceRoot, bool forceRefresh, CancellationToken cancellationToken = default);

    /// <summary>
    /// The status of ONE tool: served from the cache when it was checked within the last hour,
    /// otherwise cheaply re-probed (a single version command) and written back.
    /// </summary>
    Task<ToolStatus> GetToolStatusAsync(string workspaceRoot, string tool, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-probes one tool immediately and writes the result into the cache — used when a
    /// command's program turns out to be missing although the cache said it is installed
    /// (or the reverse).
    /// </summary>
    Task InvalidateToolAsync(string workspaceRoot, string tool, CancellationToken cancellationToken = default);

    /// <summary>
    /// The one-line per-session status for the UIs: available tools as OK, missing tools as
    /// MISSING with the official download page (git-scm.com, dotnet.microsoft.com,
    /// python.org, nodejs.org).
    /// </summary>
    static string FormatToolsLine(IReadOnlyDictionary<string, ToolStatus> tools)
    {
        static string LinkOf(string tool) => tool switch
        {
            "git" => "https://git-scm.com/downloads",
            "dotnet" => "https://dotnet.microsoft.com/download",
            "python" or "py" or "python3" => "https://www.python.org/downloads/",
            _ => "https://nodejs.org",
        };

        static string Render(ToolStatus tool) => tool.State switch
        {
            ToolStates.Available => $"{tool.Tool} OK",
            ToolStates.StoreAlias => $"{tool.Tool} STORE-ALIAS ONLY ({LinkOf(tool.Tool)})",
            _ => $"{tool.Tool} MISSING ({LinkOf(tool.Tool)})",
        };

        var order = new[] { "git", "dotnet", "python", "py", "python3", "node", "npm" };
        var parts = tools.Values
            .OrderBy(tool => Array.IndexOf(order, tool.Tool) is var at && at >= 0 ? at : int.MaxValue)
            .Select(Render)
            .ToList();
        return $"Tools: {(parts.Count == 0 ? "unknown" : string.Join(", ", parts))}";
    }
}
