using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

namespace ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;

/// <summary>
/// Root of <c>keyless.json</c>, the per-workspace configuration file.
/// All properties have defaults; the file may be partial or absent.
/// </summary>
public sealed record KeylessOptions
{
    /// <summary>
    /// How much confirmation the agent asks for. Default: <see cref="ApprovalMode.Ask"/>.
    /// </summary>
    public ApprovalMode Mode { get; init; } = ApprovalMode.Ask;

    /// <summary>
    /// Browser automation settings.
    /// </summary>
    public BrowserOptions Browser { get; init; } = new();

    /// <summary>
    /// Response size and loop limits, injected into the system prompt.
    /// </summary>
    public LimitsOptions Limits { get; init; } = new();

    /// <summary>
    /// Snapshot (workspace tree / code dump) behavior.
    /// </summary>
    public SnapshotOptions Snapshot { get; init; } = new();

    /// <summary>
    /// Git branch settings.
    /// </summary>
    public GitOptions Git { get; init; } = new();

    /// <summary>
    /// External command policy settings.
    /// </summary>
    public CommandsOptions Commands { get; init; } = new();
}

/// <summary>
/// Browser automation settings (<c>Browser</c> section).
/// </summary>
public sealed record BrowserOptions
{
    /// <summary>
    /// The Chrome remote debugging port used with <c>ConnectOverCDP</c>. Default 9222.
    /// </summary>
    public int ChromeDebugPort { get; init; } = 9222;

    /// <summary>
    /// Dedicated Chrome profile directory (mandatory since Chrome 136: the default
    /// user-data-dir ignores --remote-debugging-port). Default
    /// <c>%LOCALAPPDATA%\KeylessAgent\chrome-profile</c>. The DeepSeek login persists here.
    /// </summary>
    public string UserDataDir { get; init; } = string.Empty;

    /// <summary>
    /// Start the real Chrome with the dedicated profile automatically when the debug port
    /// is not open. Default true.
    /// </summary>
    public bool AutoLaunchChrome { get; init; } = true;

    /// <summary>
    /// Explicit path of chrome.exe. When empty, the registry (App Paths) and the common
    /// install locations are searched.
    /// </summary>
    public string ChromePath { get; init; } = string.Empty;

    /// <summary>
    /// Reuse an already-open chat.deepseek.com tab instead of opening a new one. Default true.
    /// </summary>
    public bool ReuseExistingTab { get; init; } = true;

    /// <summary>
    /// Opt-in Playwright-managed fallback browser when the real Chrome cannot be found or
    /// started. Default false (an automated browser with its own profile is confusing).
    /// </summary>
    public bool AllowPlaywrightLaunch { get; init; }

    /// <summary>
    /// Close the Chrome window the agent launched when the session ends. Default false:
    /// the profile stays warm and the login persists.
    /// </summary>
    public bool CloseChromeOnExit { get; init; }

    /// <summary>
    /// Whether the DeepThink toggle is turned on before the first message of a chat. Default true.
    /// </summary>
    public bool EnableDeepThink { get; init; } = true;

    /// <summary>
    /// Whether the Search toggle is turned on before the first message of a chat. Default true.
    /// </summary>
    public bool EnableSearch { get; init; } = true;

    /// <summary>
    /// Workspace-relative path of the editable selector catalog. Default <c>selectors.json</c>.
    /// </summary>
    public string SelectorsFile { get; init; } = "selectors.json";

    /// <summary>
    /// Where replies are read from: Auto (copy button, validated, DOM fallback — default),
    /// Copy (never silent fallback), Dom (original behavior).
    /// </summary>
    public ReplySourceMode ReplySource { get; init; } = ReplySourceMode.Auto;

    /// <summary>
    /// How long to wait for a complete reply. Default 300 seconds.
    /// </summary>
    public int ReplyTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// How long to wait for confirmation that a message was actually sent (new message item,
    /// or empty input plus generating indicator) before retrying once and failing. Default 15 seconds.
    /// </summary>
    public int SendConfirmSeconds { get; init; } = 15;

    /// <summary>
    /// How long to wait for attachment uploads to settle (chips visible, send enabled)
    /// before falling back to pasting the content. Default 60 seconds.
    /// </summary>
    public int UploadWaitSeconds { get; init; } = 60;

    /// <summary>
    /// Minimum artificial delay between browser actions. Default 400 ms.
    /// </summary>
    public int MinDelayMs { get; init; } = 0;

    /// <summary>
    /// Maximum artificial delay between browser actions. Default 1500 ms.
    /// </summary>
    public int MaxDelayMs { get; init; } = 150;

    /// <summary>
    /// Rate limit for messages sent to the chat. Default 6 per minute.
    /// </summary>
    public int MaxMessagesPerMinute { get; init; } = 6;

    /// <summary>
    /// Debug mode: saves screenshots on errors and writes a diagnostics log. Default false.
    /// </summary>
    public bool Debug { get; init; }

    /// <summary>
    /// Human-like random delays between browser actions. Default false (event-driven waits only).
    /// </summary>
    public bool HumanDelay { get; init; }
}

/// <summary>
/// Response size and loop limits (<c>Limits</c> section).
/// </summary>
public sealed record LimitsOptions
{
    /// <summary>
    /// Maximum number of non-<c>say</c> commands executed per response; the rest are reported as skipped. Default 5.
    /// </summary>
    public int MaxCommandsPerResponse { get; init; } = 5;

    /// <summary>
    /// Maximum accepted total reply length in characters. Default 60000.
    /// </summary>
    public int MaxResponseChars { get; init; } = 60000;

    /// <summary>
    /// Maximum accepted file content length per command in characters. Default 40000.
    /// </summary>
    public int MaxFileContentChars { get; init; } = 40000;

    /// <summary>
    /// Cap of command output sent back to the model (head and tail kept). Default 8000.
    /// </summary>
    public int MaxCommandOutputChars { get; init; } = 8000;

    /// <summary>
    /// How many times an invalid reply is re-requested before the turn stops. Default 3.
    /// </summary>
    public int MaxFormatRetries { get; init; } = 3;

    /// <summary>
    /// Maximum model responses processed per user request. Default 25.
    /// </summary>
    public int MaxIterationsPerTurn { get; init; } = 25;
}

/// <summary>
/// Snapshot behavior (<c>Snapshot</c> section).
/// </summary>
public sealed record SnapshotOptions
{
    /// <summary>
    /// What gets attached to a user request. Default <see cref="SnapshotMode.Full"/>.
    /// </summary>
    public SnapshotMode Mode { get; init; } = SnapshotMode.Full;

    /// <summary>
    /// Whether the snapshot is also attached to result messages. Default false.
    /// </summary>
    public bool AttachOnFeedback { get; init; }

    /// <summary>
    /// Whether the bat files also run after every executed batch. Default false.
    /// </summary>
    public bool RefreshAfterEachBatch { get; init; }

    /// <summary>
    /// Noise filtering applied to the attached copy only, never to files on disk.
    /// </summary>
    public SnapshotFilterOptions Filter { get; init; } = new();
}

/// <summary>
/// Configurable noise filters for snapshot attachments (<c>Snapshot.Filter</c> section).
/// </summary>
public sealed record SnapshotFilterOptions
{
    /// <summary>
    /// Tree-view line filters: lines mentioning any of these folder names are dropped from the attached copy.
    /// </summary>
    public IReadOnlyList<string> ExcludeTreePatterns { get; init; } = ["bin", "obj", ".git", "node_modules", ".agent", "artifacts"];

    /// <summary>
    /// Code-dump section filters: sections whose <c>###</c> heading matches any of these are dropped.
    /// </summary>
    public IReadOnlyList<string> ExcludeDumpHeadings { get; init; } = ["tree-view-result", "code-dump-result"];
}

/// <summary>
/// Git settings (<c>Git</c> section).
/// </summary>
public sealed record GitOptions
{
    /// <summary>
    /// Automatically initialize a git repository (with .gitignore and an initial commit) when
    /// the workspace is not one yet. Default true.
    /// </summary>
    public bool AutoInit { get; init; } = true;

    /// <summary>
    /// Prefix of agent branches. Default <c>agent/</c>.
    /// </summary>
    public string BranchPrefix { get; init; } = "agent/";

    /// <summary>
    /// Branch name patterns (case-insensitive, <c>*</c> wildcard) that must never be modified. Default main, master, release*.
    /// </summary>
    public IReadOnlyList<string> ProtectedBranchPatterns { get; init; } = ["main", "master", "release*"];
}

/// <summary>
/// External command policy settings (<c>Commands</c> section).
/// </summary>
public sealed record CommandsOptions
{
    /// <summary>
    /// Program names allowed to run in <c>AutoSafe</c> mode.
    /// </summary>
    public IReadOnlyList<string> Allowlist { get; init; } = ["dotnet", "git", "npm", "node", "python"];

    /// <summary>
    /// Command tiers that run without confirmation in Ask mode. Default: ReadOnly and Build.
    /// </summary>
    public IReadOnlyList<string> AutoApprove { get; init; } = ["ReadOnly", "Build"];

    /// <summary>
    /// What happens after a failed command: StopOnMutationFailure (default; a ReadOnly probe
    /// failure never stops, everything else does), StopOnAnyFailure, Continue.
    /// </summary>
    public string BatchFailureMode { get; init; } = "StopOnMutationFailure";

    /// <summary>
    /// Timeout for every external process. Default 300 seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 300;
}
