using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Factory;
using Microsoft.Extensions.Logging;

namespace ImanSoftware.KeylessAgent.Wpf.Services;

/// <summary>
/// Creates the real agent loop through the Core service factory — the same construction path
/// the CLI uses. Tests never touch this class; they inject fakes into <see cref="AgentRunner"/>.
/// </summary>
internal static class AgentLoopFactory
{
    /// <summary>Creates an agent loop for a workspace.</summary>
    /// <param name="workspaceRoot">Workspace directory; also the log location when verbose.</param>
    /// <param name="verbose">Enables the file logger (<c>.agent/logs/diagnostics.log</c>).</param>
    public static IAgentLoop Create(string workspaceRoot, bool verbose)
        => KeylessAgentServiceFactory.CreateAgentLoop(verbose ? KeylessAgentServiceFactory.CreateFileLoggerFactory(workspaceRoot) : null);
}
