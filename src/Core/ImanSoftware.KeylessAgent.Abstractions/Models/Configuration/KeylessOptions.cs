using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;

namespace ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;

/// <summary>
/// Root of <c>keyless.json</c>, the per-workspace configuration file.
/// All properties have defaults; the file may be partial or absent.
/// </summary>
public sealed record KeylessOptions
{
    /// <summary>The current keyless.json schema version.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// The keyless.json schema version this file was written for. Null means the file was
    /// written before versioning existed (schema version 1) and it will be migrated on load.
    /// </summary>
    public int? SchemaVersion { get; init; }

    /// <summary>
    /// Creates the current default configuration with the schema version set, for fresh
    /// workspaces and for the schema migration.
    /// </summary>
    public static KeylessOptions CreateDefault() => new() { SchemaVersion = CurrentSchemaVersion };


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

    /// <summary>
    /// Pacing of the model's replies: how many non-say commands one response may carry.
    /// </summary>
    public AgentOptions Agent { get; init; } = new();
}

/// <summary>
/// The <c>Agent</c> section: reply pacing. The effective per-response command limit is
/// min(Pace limit, Limits.MaxCommandsPerResponse).
/// </summary>
public sealed record AgentOptions
{
    /// <summary>
    /// Step = 1 command per response, Balanced = 3 (default), Batch = Limits.MaxCommandsPerResponse.
    /// </summary>
    public AgentPace Pace { get; init; } = AgentPace.Balanced;
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
    /// Maximum turns per chat before opening a new chat automatically. Default 20.
    /// </summary>
    public int MaxTurnsPerChat { get; init; } = 20;

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
    /// How long to wait for a complete reply. Default 180 seconds.
    /// </summary>
    public int ReplyTimeoutSeconds { get; init; } = 180;

    /// <summary>
    /// How long to wait for confirmation that a message was actually sent (new message item,
    /// or empty input plus generating indicator) before retrying once and failing. Default 8 seconds.
    /// </summary>
    public int SendConfirmSeconds { get; init; } = 8;

    /// <summary>
    /// How long to wait for attachment uploads to settle (chips visible, send enabled)
    /// before falling back to pasting the content. Default 15 seconds.
    /// </summary>
    public int UploadWaitSeconds { get; init; } = 15;

    /// <summary>
    /// When the machine is offline before a send, wait up to this many seconds for the
    /// connection to come back (polling every 2 seconds) instead of failing immediately.
    /// Default 0 — fail at once with KA_NETWORK_OFFLINE.
    /// </summary>
    public int WaitForNetworkSeconds { get; init; } = 0;

    /// <summary>
    /// Minimum artificial delay between browser actions. Default 400 ms.
    /// </summary>
    public int MinDelayMs { get; init; } = 400;

    /// <summary>
    /// Maximum artificial delay between browser actions. Default 1500 ms.
    /// </summary>
    public int MaxDelayMs { get; init; } = 1500;

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

    /// <summary>
    /// Browser window behavior. Accepted values: "Offscreen", "Minimized", "TinyCorner",
    /// "EdgePeek", "Normal". Default "TinyCorner": a visible-but-imperceptible 80x80 window
    /// in the bottom-right corner of the primary screen. The window MUST be visible-on-screen
    /// (at least 80x80 pixels) to prevent Chrome's occlusion throttling — fully offscreen
    /// positions (-32000,-32000, "Offscreen"/"Minimized") and truly minimized windows are
    /// treated as occluded by Chrome 130+ on Windows 11 24H2 regardless of the
    /// --disable-features=CalculateNativeWinOcclusion flag, and the renderer throttle
    /// truncates DeepSeek streaming replies (turn-17/18 evidence). Normal = never forced.
    /// Only affects auto-launched Chrome; every hide path uses the same tiny-corner geometry.
    /// </summary>
    public string WindowMode { get; init; } = "TinyCorner";

    /// <summary>
    /// Minimize the window again after the login completes. Default true.
    /// </summary>
    public bool MinimizeAfterLogin { get; init; } = true;

    /// <summary>
    /// Un-minimize the chat window ONCE when a throttled reply stream has been rejected for
    /// a quarter of TruncationBudgetSeconds in a single reply wait. Default false: the
    /// window stays hidden for the whole turn (focusing it steals the terminal from the user).
    /// </summary>
    public bool AutoFocusOnTruncation { get; init; }

    /// <summary>
    /// Wall-clock budget for a THROTTLED reply stream: when truncation detections span
    /// longer than this (time since the FIRST detection, not an attempt count), the reply
    /// wait fails with KA_REPLY_TRUNCATED. Default 20 seconds - enough for a stream that
    /// resumes after a few real seconds, quick enough to fail a permanently stuck one.
    /// </summary>
    public int TruncationBudgetSeconds { get; init; } = 20;

    /// <summary>
    /// Upper bound of the positive-first login probe: wait this long for EITHER the chat
    /// input (logged in) OR a login indicator, in one parallel wait. Default 15 seconds.
    /// </summary>
    public int LoginProbeSeconds { get; init; } = 15;
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
    /// The context mode of the FIRST user-request message after the app/process starts or in
    /// a new chat. Default <see cref="ContextMode.Snapshot"/> (tree + full budgeted dump).
    /// </summary>
    public ContextMode FirstMessageMode { get; init; } = ContextMode.Snapshot;

    /// <summary>
    /// The context mode of every later message. Default <see cref="ContextMode.Changed"/>
    /// (files changed since the last context build, with content). When it finds nothing the
    /// message carries no attachment and notes "no file changes since last message".
    /// </summary>
    public ContextMode DefaultMode { get; init; } = ContextMode.Changed;

    /// <summary>
    /// Maximum number of explicit paths one @@context files attachment may carry.
    /// Default 20.
    /// </summary>
    public int MaxContextFiles { get; init; } = 20;

    /// <summary>
    /// Total character ceiling for everything attached to ONE turn (tree + dump + notes
    /// combined; chars/4 is a rough token estimate for DeepSeek). When exceeded: the tree is
    /// always kept, the dump keeps only files referenced by the turn's request (plus the
    /// TreeAndChanged diff when active), and everything else becomes a one-line omission
    /// note pointing at the file-read tool. Default 400,000.
    /// </summary>
    public int MaxTotalAttachmentChars { get; init; } = 400_000;

    /// <summary>
    /// Per-file cap for the attached code dump: a file whose fenced content exceeds this is
    /// replaced by its heading plus a "[file too large to attach: N KB …]" placeholder line,
    /// keeping the file's existence visible without its full content. Default 50,000.
    /// </summary>
    public int MaxDumpedFileChars { get; init; } = 50_000;

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

    /// <summary>
    /// Generated/vendored files excluded from the ATTACHED dump by file-name wildcard
    /// (case-insensitive <c>*</c>, like SensitiveFilePatterns): minified bundles, lock files
    /// and generated designer/migration snapshots are huge and rarely useful to attach.
    /// </summary>
    public IReadOnlyList<string> ExcludeFilePatterns { get; init; } =
    [
        "*.min.js", "*.min.css", "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
        "*.designer.cs", "*.g.cs", "*.g.i.cs", "*ModelSnapshot.cs",
    ];

    /// <summary>
    /// Files whose sections are dropped from the ATTACHED copy entirely (never from disk):
    /// case-insensitive <c>*</c> wildcards matched against the file name. Environment files,
    /// key material and anything whose name suggests credentials.
    /// </summary>
    public IReadOnlyList<string> SensitiveFilePatterns { get; init; } =
    [
        ".env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12", "id_rsa*", "*.keystore",
        "secrets.json", "*secret*", "*password*", "*credential*", "appsettings.Production.json",
        "appsettings.*.Production.json",
    ];

    /// <summary>
    /// Replace secret-looking VALUES in the remaining attached sections with [redacted]
    /// (Password=/Pwd=/ApiKey/… assignments, JSON secret keys, Authorization tokens, PEM
    /// private-key blocks). Key names and structure are kept. Default true.
    /// </summary>
    public bool MaskSensitiveLines { get; init; } = true;
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
    /// Timeout for every external process. Default 180 seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 180;
}
