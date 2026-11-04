 Fires after a quarter of **TruncationBudgetSeconds** has elapsed, not after a fixed attempt count.# Configuration

KeylessAgent reads `keyless.json` from the workspace root. The first run writes the full default file so you can edit it. Parsing is **tolerant**: comments (`//`), case-insensitive keys, trailing commas, and string enums (`"Ask"`, `"AutoSafe"`, …) all work. Invalid JSON or out-of-range values fail with a precise message instead of an exception.

```jsonc
{
  // How much confirmation the agent asks for.
  "Mode": "Ask",            // Ask | AutoSafe | Auto

  // Schema version of this file; absent = v1. The loader migrates stale files to
  // CurrentSchemaVersion (backing up the original as keyless.json.v<N>.bak).
  "SchemaVersion": 2,

  "Browser": {
    "ChromeDebugPort": 9222,        // CDP port; ignored by Chrome's DEFAULT profile since Chrome 136
    "UserDataDir": "",              // dedicated profile; empty = %LOCALAPPDATA%\KeylessAgent\chrome-profile
    "AutoLaunchChrome": true,       // start the real Chrome with the dedicated profile when the port is closed
    "ChromePath": "",               // explicit chrome.exe path; empty = registry + common locations
    "ReuseExistingTab": true,       // reuse an open chat.deepseek.com tab instead of opening one
    "AllowPlaywrightLaunch": false, // opt-in automated fallback browser (NOT your Chrome)
    "CloseChromeOnExit": false,     // false = keep the chat tab/window open across turns and runs
    "EnableDeepThink": true,        // DeepThink toggle before the first message
    "EnableSearch": true,           // Search toggle before the first message
    "SelectorsFile": "selectors.json",
    "ReplyTimeoutSeconds": 180,
    "ReplySource": "Auto",           // Auto (copy button + validation + DOM fallback) | Copy | Dom
    "WindowMode": "TinyCorner",      // TinyCorner (default) | EdgePeek | Offscreen | Minimized | Normal (auto-launched Chrome only)
    "MinimizeAfterLogin": true,      // minimize the window after login completes
    "AutoFocusOnTruncation": false,  // un-minimize once when a throttled stream was rejected 5x (default: stay minimized)
    "TruncationBudgetSeconds": 20,   // wall-clock budget for a throttled stream before the wait fails (KA_REPLY_TRUNCATED)
    "LoginProbeSeconds": 15,         // positive-first probe: wait for InputBox OR login indicator
    "MaxTurnsPerChat": 20,           // turns per chat before a new chat starts automatically
    "HumanDelay": false,             // random delays between browser actions (default: event-driven only)
    "SendConfirmSeconds": 8,         // wait for proof the message was sent before retrying once
    "UploadWaitSeconds": 15,         // wait for attachment chips + enabled send before pasting
    "WaitForNetworkSeconds": 0,      // when offline before a send: poll up to N s for the connection (0 = fail at once)
    "MinDelayMs": 400,              // random delay between actions (default: event-driven only)
    "MaxDelayMs": 1500,
    "MaxMessagesPerMinute": 6,      // local rate limiter
    "Debug": false                  // screenshots on errors
  },

  "Limits": {                       // injected into the system prompt; enforced in code
    "MaxCommandsPerResponse": 5,
    "MaxResponseChars": 60000,
    "MaxFileContentChars": 40000,
    "MaxCommandOutputChars": 8000,
    "MaxFormatRetries": 3,
    "MaxIterationsPerTurn": 25
  },

  "Snapshot": {
    "Mode": "TreeAndChanged",       // TreeAndChanged (default) | Full
    "MaxTotalAttachmentChars": 400000, // total ceiling for ONE turn's attachments (tree + dump)
    "MaxDumpedFileChars": 50000,    // per-file cap in the attached dump (placeholder beyond it)
    "AttachOnFeedback": false,      // also attach the snapshot to result messages
    "RefreshAfterEachBatch": false,
    "Filter": {
      "ExcludeTreePatterns": [ "bin", "obj", ".git", "node_modules", ".agent", "artifacts" ],
      "ExcludeDumpHeadings": [ "tree-view-result", "code-dump-result" ],
      "ExcludeFilePatterns": [ "*.min.js", "*.min.css", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "*.designer.cs", "*.g.cs", "*.g.i.cs", "*ModelSnapshot.cs" ],
      "SensitiveFilePatterns": [ ".env", ".env.*", "*.pem", "*.key", "*.pfx", "*.p12", "id_rsa*", "*.keystore", "secrets.json", "*secret*", "*password*", "*credential*", "appsettings.Production.json", "appsettings.*.Production.json" ],
      "MaskSensitiveLines": true
    }
  },

  "Git": {
    "AutoInit": true,               // auto git init + .gitignore + initial commit when the workspace is not a repo
    "BranchPrefix": "agent/",
    "ProtectedBranchPatterns": [ "main", "master", "release*" ]
  },

  "Agent": {
    "Pace": "Balanced"            // Step = 1 command per response, Balanced = 3, Batch = Limits.MaxCommandsPerResponse
  },

  "Commands": {
    "Allowlist": [ "dotnet", "git", "npm", "node", "python" ],
    "AutoApprove": [ "ReadOnly", "Build" ],
    "BatchFailureMode": "StopOnMutationFailure",
    "TimeoutSeconds": 180
  }
}
```

## Sections

### Mode (approval)

- **Ask** *(default)* — every `runcommand` and every `deletefile` needs your confirmation; file changes show a colored diff first.
- **AutoSafe** — allow-listed `runcommand`s run automatically, everything else (including every `deletefile`) still asks. A warning is shown at turn start: **AutoSafe is not a security boundary** — allow-listed programs (dotnet, node, python, …) can execute arbitrary code.
- **Auto** — everything runs automatically, including `deletefile` (the deny list still applies); `deletefile` is the only confirmation Auto skips. Backups are always made.

### Browser — the Chrome 136 rule

Since Chrome 136, `--remote-debugging-port` is **ignored when Chrome runs with its default
user-data-dir**. Attaching to a daily Chrome profile is impossible; a dedicated profile is
mandatory. KeylessAgent therefore uses one dedicated profile and one login:

1. **Probe**: `http://127.0.0.1:<ChromeDebugPort>/json/version` — if it answers, attach ("Attached to Chrome on port N").
2. **Auto-launch** (default on): start the REAL Chrome with `--remote-debugging-port … --user-data-dir=<dedicated profile> --no-first-run --no-default-browser-check`, opening chat.deepseek.com, wait up to 30s for the port, attach. Log in to DeepSeek **once** in that window; the login persists in the profile.
3. **Playwright fallback**: only with `AllowPlaywrightLaunch: true` (default false) — a separate automated browser, clearly warned.
4. If Chrome cannot be found or started, the agent fails with `KA_BROWSER_CONNECTION_FAILED` and prints the exact manual-start command line.

Tab rules: an existing `chat.deepseek.com` tab is reused (`ReuseExistingTab`); otherwise a new TAB is opened in the existing window (`Target.createTarget … newWindow:false`). The agent never navigates your other tabs. The chosen tab is focused (`BringToFrontAsync` + `Target.activateTarget`) only when the user must act (login page detected).

**Login detection is positive-first**: one parallel wait races the chat InputBox against the login indicators (`LoginProbeSeconds`, default 15 — all selectors in a single combined wait, never one timeout per fallback). If the input appears first you are logged in and no login wait happens; only a winning login indicator focuses the tab and waits for the manual login (5 minutes, polled with instant checks). Absent elements cost their wait once — not once per fallback.

**Fast waits by design**: presence questions (stop indicator, chat-limit notice, chips, toggles, send button) are answered with instant single-round-trip checks, and reply polling runs at a ~100 ms cadence. `keyless run … --timing` prints the per-phase timing table (login probe, toggles, upload, send, send-confirm, first token, completion, copy, parse, execute, results-send) after the turn; the same table is always appended to `.agent/logs/turn-N.jsonl`.

Dispose policy: only the tab the agent opened is closed; the Chrome window stays running by default (`CloseChromeOnExit: false`) so the next run reuses the warm session.

`keyless chrome <workspace>` starts only the dedicated Chrome (for logging in first), and `keyless doctor <workspace>` reports which connection path was taken.
- **Toggles**: before the first message of a new chat the agent reads each toggle's `aria-pressed` and only clicks when it disagrees with the config; if a toggle cannot be found it warns and continues.
- **Debug/verbose** (`keyless run … -v` or `Browser.Debug`): writes `.agent/logs/diagnostics.log` and saves screenshots on errors.

### Limits

See [agentscript-spec.md](agentscript-spec.md#limits-enforced-injected-into-the-system-prompt).


### Browser window

- **WindowMode**: `TinyCorner` (default), `EdgePeek`, `Offscreen`, `Minimized` (backward-compatibility alias of offscreen), or `Normal`. Affects auto-launched Chrome only and the hide geometry. The window must be visible-on-screen (at least 80x80 pixels) to prevent Chrome's occlusion throttling: fully offscreen positions do NOT work on Chrome 130+ / Windows 11 24H2 even with `--disable-features=CalculateNativeWinOcclusion` — the renderer throttles and truncates the DeepSeek stream (turn-17/18 evidence). TinyCorner keeps an 80x80 square in the bottom-right corner (primary screen bounds, not hardcoded); EdgePeek uses a 10x10 sliver in the corner (may still be throttled if Chrome's heuristic rejects it). Anti-throttling flags (`--disable-renderer-backgrounding`, `--disable-background-timer-throttling`, `--disable-backgrounding-occluded-windows`, `--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling`) are always passed; verify the running Chrome with `keyless diagnostics chrome`.
- **TruncationBudgetSeconds** (default 20): wall-clock budget for a throttled reply stream - the wait keeps polling (spaced by the normal poll delay) while the stream may still resume, and fails with KA_REPLY_TRUNCATED only when the time since the FIRST truncation detection exceeds this. The reply-truncated turn event carries ElapsedSeconds so the logs show real timing.
- **MinimizeAfterLogin**: minimize the window again after login completes (default true). The window is restored ONLY for login/captcha.

### Snapshot — sensitive content never reaches the chat

- `Snapshot.Filter.SensitiveFilePatterns` (case-insensitive `*` wildcards against the file name) drops dump sections of secret files from the ATTACHED copy: environment files (`.env`), key material (`.pem`, `.key`, `.pfx`, `.p12`, `id_rsa*`, `*.keystore`), and anything named `*secret*`/`*password*`/`*credential*`/`appsettings.Production.json` by default.
- `Snapshot.Filter.MaskSensitiveLines` (default true) redacts secret-looking VALUES in the remaining sections: `Password=…`, `"ApiKey": "…"`, `Authorization: Bearer …` and PEM private-key blocks become `[redacted]` — key names and structure are kept.
- The tree attachment reports it: `[N files filtered and M values masked as sensitive]`.
- **Attachment copy only** — the files on disk (including `code-dump-result.md` and `tree-view-result.txt`) are never modified.

### Snapshot

- The snapshots are **generated natively in C#** — no `.bat` files need to exist in the workspace. `tree-view-result.txt` and `code-dump-result.md` are written into the workspace root (and excluded from git via `.git/info/exclude`).
- Filtering applies to the **attached copy only** — `tree-view-result.txt` and `code-dump-result.md` on disk are never modified.
- Oversized dumps are split at `### heading` boundaries into `code-dump-result-part1.md`, `-part2.md`, … and uploaded as consecutive files.
- **Snapshot.Mode** is `TreeAndChanged` by default: the tree plus the files changed since the previous refresh (the diff is stashed BEFORE the baseline is overwritten, so it really contains the agent's changes). `Full` attaches everything, for anyone who explicitly wants it.
- **First turn / no usable baseline / empty diff**: the attachment falls back to the tree plus a BUDGET-LIMITED dump — never an unlimited full dump. The next turn has a real baseline and diffs normally.
- **Snapshot.MaxTotalAttachmentChars** (default 400,000 ≈ chars/4 tokens) caps EVERYTHING attached to one turn. When exceeded: the tree stays complete, the dump keeps only files referenced by the turn's request text, and every other file becomes a per-folder note (`[omitted: N files under …/ (N KB) — use the file-read tool …]`). A `snapshot-budget-exceeded` turn event records the original total, the ceiling and the omissions.
- **Snapshot.MaxDumpedFileChars** (default 50,000) caps each file's fenced content in the dump: oversized files keep their `###` heading plus `[file too large to attach: N KB — use the file-read tool …]`.
- **Snapshot.Filter.ExcludeFilePatterns** (case-insensitive `*` wildcards against the file name) drop generated/vendored files from the attached dump entirely: `*.min.js`, `*.min.css`, `package-lock.json`, `yarn.lock`, `pnpm-lock.yaml`, `*.designer.cs`, `*.g.cs`, `*.g.i.cs`, `*ModelSnapshot.cs` by default. Disk copies are never touched.

### Git

Protected patterns are matched case-insensitively with `*` wildcards. The guard runs **in code** before every file-changing command and every commit/push.

### Commands

The allow list matches the first program name of the command line. The built-in deny list (format/shutdown/force-push/`git config --global`/`curl | sh`/recursive deletes/encoded PowerShell/…) always applies and cannot be configured away.

**Risk tiers** classify every command segment: ReadOnly (git status, dotnet --version, node -v, dir, type, cat, echo, …), Build (dotnet build/restore/clean), Execute (dotnet test/run, npm test/run, node file, python file, …), Risky (inline interpreters, redirection, command substitution, package installs, deletes/moves/copies, git writes, workspace-escaping paths, unknown programs — always confirmed even in AutoSafe), Denied (the built-in deny list — never runs).

**AutoApprove** lists the tiers that run without confirmation in Ask mode (default: ReadOnly and Build). **BatchFailureMode** controls what happens after a failed command: `StopOnMutationFailure` (default — a ReadOnly probe failure never stops the batch, everything else does), `StopOnAnyFailure`, or `Continue`. Failed command output is always sent to the model so it can adapt.

## Other files

- `selectors.json` — the editable chat selector catalog (see [browser-automation.md](browser-automation.md)); created with defaults on first run.
- `.agent/session.json` — the saved chat URL, branch and turn counter (powers `keyless resume`). A corrupt file never crashes a turn: it is renamed to `session.json.corrupt` and treated as no session.
- `.agent/logs/turn-N.jsonl` — the per-turn transcript is best-effort: the first write failure disables it for the rest of the turn with one warning. Command **output is redacted before writing** (`Password=…`, tokens, Bearer/Basic values → `[redacted]`); requests and replies are stored as received.
- `.agent/backups/` — timestamped backups of every changed file (powers `keyless undo`).
- `.git/info/exclude` — the agent adds `.agent/`, `tree-view-result.txt` and `code-dump-result.md` automatically.
