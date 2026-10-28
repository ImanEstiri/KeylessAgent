# Configuration

KeylessAgent reads `keyless.json` from the workspace root. The first run writes the full default file so you can edit it. Parsing is **tolerant**: comments (`//`), case-insensitive keys, trailing commas, and string enums (`"Ask"`, `"AutoSafe"`, …) all work. Invalid JSON or out-of-range values fail with a precise message instead of an exception.

```jsonc
{
  // How much confirmation the agent asks for.
  "Mode": "Ask",            // Ask | AutoSafe | Auto

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
    "ReplyTimeoutSeconds": 300,
    "ReplySource": "Auto",           // Auto (copy button + validation + DOM fallback) | Copy | Dom
    "WindowMode": "Minimized",       // Minimized | Offscreen | Normal (auto-launched Chrome only)
    "MinimizeAfterLogin": true,      // minimize the window after login completes
    "HumanDelay": false,             // random delays between browser actions (default: event-driven only)
    "SendConfirmSeconds": 15,        // wait for proof the message was sent before retrying once
    "UploadWaitSeconds": 60,         // wait for attachment chips + enabled send before pasting
    "MinDelayMs": 400,              // random human-like delay between actions
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
    "Mode": "Full",                 // Full | TreeAndChanged
    "AttachOnFeedback": false,      // also attach the snapshot to result messages
    "RefreshAfterEachBatch": false,
    "Filter": {
      "ExcludeTreePatterns": [ "bin", "obj", ".git", "node_modules", ".agent", "artifacts" ],
      "ExcludeDumpHeadings": [ "tree-view-result", "code-dump-result" ]
    }
  },

  "Git": {
    "AutoInit": true,               // auto git init + .gitignore + initial commit when the workspace is not a repo
    "MaxTurnsPerChat": 20,          // (in Browser section) turns per chat before a new chat starts
    "BranchPrefix": "agent/",
    "ProtectedBranchPatterns": [ "main", "master", "release*" ]
  },

  "Commands": {
    "Allowlist": [ "dotnet", "git", "npm", "node", "python" ],
    "AutoApprove": [ "ReadOnly", "Build" ],
    "BatchFailureMode": "StopOnMutationFailure",
    "TimeoutSeconds": 300
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

Tab rules: an existing `chat.deepseek.com` tab is reused (`ReuseExistingTab`); otherwise a new TAB is opened in the existing window (`Target.createTarget … newWindow:false`). The agent never navigates your other tabs. The chosen tab is focused (`BringToFrontAsync` + `Target.activateTarget`), also when a login is required.

Dispose policy: only the tab the agent opened is closed; the Chrome window stays running by default (`CloseChromeOnExit: false`) so the next run reuses the warm session.

`keyless chrome <workspace>` starts only the dedicated Chrome (for logging in first), and `keyless doctor <workspace>` reports which connection path was taken.
- **Toggles**: before the first message of a new chat the agent reads each toggle's `aria-pressed` and only clicks when it disagrees with the config; if a toggle cannot be found it warns and continues.
- **Debug/verbose** (`keyless run … -v` or `Browser.Debug`): writes `.agent/logs/diagnostics.log` and saves screenshots on errors.

### Limits

See [agentscript-spec.md](agentscript-spec.md#limits-enforced-injected-into-the-system-prompt).


### Browser window

- **WindowMode**: `Minimized` (default), `Offscreen`, or `Normal`. Affects auto-launched Chrome only. When minimized, throttling flags (`--disable-renderer-backgrounding`, `--disable-background-timer-throttling`, `--disable-backgrounding-occluded-windows`) prevent slow-downs.
- **MinimizeAfterLogin**: minimize the window again after login completes (default true). The window is restored ONLY for login/captcha.

### Snapshot

- The snapshots are **generated natively in C#** — no `.bat` files need to exist in the workspace. `tree-view-result.txt` and `code-dump-result.md` are written into the workspace root (and excluded from git via `.git/info/exclude`).
- Filtering applies to the **attached copy only** — `tree-view-result.txt` and `code-dump-result.md` on disk are never modified.
- Oversized dumps are split at `### heading` boundaries into `code-dump-result-part1.md`, `-part2.md`, … and uploaded as consecutive files.

### Git

Protected patterns are matched case-insensitively with `*` wildcards. The guard runs **in code** before every file-changing command and every commit/push.

### Commands

The allow list matches the first program name of the command line. The built-in deny list (format/shutdown/force-push/`git config --global`/`curl | sh`/recursive deletes/encoded PowerShell/…) always applies and cannot be configured away.

**Risk tiers** classify every command segment: ReadOnly (git status, dotnet --version, node -v, dir, type, cat, echo, …), Build (dotnet build/restore/clean), Execute (dotnet test/run, npm test/run, node file, python file, …), Risky (inline interpreters, redirection, command substitution, package installs, deletes/moves/copies, git writes, workspace-escaping paths, unknown programs — always confirmed even in AutoSafe), Denied (the built-in deny list — never runs).

**AutoApprove** lists the tiers that run without confirmation in Ask mode (default: ReadOnly and Build). **BatchFailureMode** controls what happens after a failed command: `StopOnMutationFailure` (default — a ReadOnly probe failure never stops the batch, everything else does), `StopOnAnyFailure`, or `Continue`. Failed command output is always sent to the model so it can adapt.

## Other files

- `selectors.json` — the editable chat selector catalog (see [browser-automation.md](browser-automation.md)); created with defaults on first run.
- `.agent/session.json` — the saved chat URL, branch and turn counter (powers `keyless resume`).
- `.agent/backups/` — timestamped backups of every changed file (powers `keyless undo`).
- `.agent/logs/turn-N.jsonl` — full per-turn transcript (messages, replies, commands, results).
- `.git/info/exclude` — the agent adds `.agent/`, `tree-view-result.txt` and `code-dump-result.md` automatically.
