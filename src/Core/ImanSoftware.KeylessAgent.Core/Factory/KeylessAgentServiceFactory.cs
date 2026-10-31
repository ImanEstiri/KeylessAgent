using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Agent;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;
using ImanSoftware.KeylessAgent.Core.Implementation.Git;
using ImanSoftware.KeylessAgent.Core.Implementation.Parsing;
using ImanSoftware.KeylessAgent.Core.Implementation.Snapshot;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImanSoftware.KeylessAgent.Core.Factory;

/// <summary>
/// Hand-written wiring for every KeylessAgent service — no DI container.
/// UIs call <see cref="CreateAgentLoop(ILoggerFactory?)"/> and drive it through <see cref="IAgentUserInterface"/>.
/// </summary>
public static class KeylessAgentServiceFactory
{
    /// <summary>
    /// Creates the agent loop with the real browser chat provider.
    /// </summary>
    /// <param name="loggerFactory">Optional logger factory; defaults to the no-op factory.</param>
    /// <returns>The agent loop, ready to run turns.</returns>
    public static IAgentLoop CreateAgentLoop(ILoggerFactory? loggerFactory = null)
        => CreateAgentLoop(new PlaywrightChatProvider(loggerFactory, new ImanSoftware.KeylessAgent.Core.Implementation.Browser.ReplyExtractor()), loggerFactory);

    /// <summary>
    /// Creates the agent loop with a custom chat provider (fake provider in tests and the Playground).
    /// </summary>
    /// <param name="chatProvider">The chat provider to use.</param>
    /// <param name="loggerFactory">Optional logger factory; defaults to the no-op factory.</param>
    /// <returns>The agent loop, ready to run turns.</returns>
    public static IAgentLoop CreateAgentLoop(IChatProvider chatProvider, ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        return new AgentLoop(chatProvider, new ImanSoftware.Logging.LoggerAdapter<AgentLoop>(loggerFactory));
    }

    /// <summary>
    /// Creates the configuration service.
    /// </summary>
    /// <returns>The configuration service.</returns>
    public static IConfigurationService CreateConfigurationService()
        => new ConfigurationService();

    /// <summary>
    /// Creates the git service.
    /// </summary>
    /// <returns>The git service.</returns>
    public static IGitService CreateGitService()
        => new GitService();

    /// <summary>
    /// Creates the backup service (powers <c>keyless undo</c>).
    /// </summary>
    /// <returns>The backup service.</returns>
    public static IBackupService CreateBackupService()
        => new BackupService();

    /// <summary>
    /// Creates the snapshot service.
    /// </summary>
    /// <returns>The snapshot service.</returns>
    public static ISnapshotService CreateSnapshotService()
        => new SnapshotService();

    /// <summary>
    /// Creates a logger factory that appends diagnostics to <c>.agent/logs/diagnostics.log</c>
    /// (used by the CLI in verbose/debug mode).
    /// </summary>
    /// <param name="workspaceRoot">The workspace directory.</param>
    /// <returns>The logger factory.</returns>
    public static ILoggerFactory CreateFileLoggerFactory(string workspaceRoot)
    {
        var storage = ImanSoftware.FileStorage.ImanFileStorageFactory.CreateStorage();
        var provider = new ImanSoftware.KeylessAgent.Core.Implementation.Diagnostics.FileLoggerProvider(
            workspaceRoot, storage);
        return new SingleProviderLoggerFactory(provider);
    }

    /// <summary>
    /// Creates the session store (<c>.agent/session.json</c>, powers <c>keyless resume</c>).
    /// </summary>
    /// <returns>The session store.</returns>
    public static ISessionStore CreateSessionStore()
        => new SessionStore();

    /// <summary>
    /// Creates the workspace capability inspector (probed once per session).
    /// </summary>
    /// <returns>The workspace inspector.</returns>
    public static IWorkspaceInspector CreateWorkspaceInspector()
        => new WorkspaceInspector();

    /// <summary>
    /// Creates the change tracker (git porcelain or no-git index).
    /// </summary>
    /// <returns>The change tracker.</returns>
    public static IChangeTracker CreateChangeTracker()
        => new ChangeTracker();

    /// <summary>
    /// Creates the browser connection service (probe/launch/attach/tab rules) shared by
    /// run, doctor and the <c>keyless chrome</c> command.
    /// </summary>
    /// <returns>The browser connector.</returns>
    public static IBrowserConnector CreateBrowserConnector()
        => new BrowserConnector();

    /// <summary>
    /// Creates the chat diagnostics service (powers <c>keyless doctor</c>; a future GUI can reuse it).
    /// </summary>
    /// <returns>The diagnostics service.</returns>
    public static IChatDiagnostics CreateChatDiagnostics()
        => new ChatDiagnosticsService();

    /// <summary>
    /// Creates the AgentScript parser.
    /// </summary>
    /// <returns>The parser.</returns>
    public static IAgentScriptParser CreateParser()
        => new AgentScriptParser();
}
