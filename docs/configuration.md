# Configuration

KeylessAgent reads `keyless.json` from the workspace root. The first run writes the full default file so you can edit it. Parsing is **tolerant**: comments (`//`), case-insensitive keys, trailing commas, and string enums (`"Ask"`, `"AutoSafe"`, …) all work. Invalid JSON or out-of-range values fail with a precise message instead of an exception.

```jsonc
{
  // How much confirmation the agent asks for.
  "Mode": "Ask",            // Ask | AutoSafe | Auto

  "Browser": {
    "ChromeDebugPort": 9222,        // CDP port of your running Chrome
    "EnableDeepThink": true,        // DeepThink toggle before the first message
    "EnableSearch": true,           // Search toggle before the first message
    "SelectorsFile": "selectors.json",
    "ReplyTimeoutSeconds": 300,
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
    "BranchPrefix": "agent/",
    "ProtectedBranchPatterns": [ "main", "master", "release*" ]
  },

  "Commands": {
    "Allowlist": [ "dotnet", "git", "npm", "node", "python" ],
    "TimeoutSeconds": 300
  }
}
```

## Sections

### Mode (approval)

- **Ask** *(default)* — every `runcommand` and `deletefile` needs your confirmation; file changes show a colored diff first.
- **AutoSafe** — allow-listed commands run automatically; everything else still asks.
- **Auto** — everything runs automatically (the deny list still applies), with a warning.

### Browser

- **ChromeDebugPort**: start Chrome with `chrome --remote-debugging-port=9222`; KeylessAgent attaches over CDP to your real profile. If that fails it launches a persistent Chromium profile under `%LOCALAPPDATA%\KeylessAgent\chrome-profile` (log in once; it stays logged in).
- **Toggles**: before the first message of a new chat the agent reads each toggle's `aria-pressed` and only clicks when it disagrees with the config; if a toggle cannot be found it warns and continues.
- **Debug/verbose** (`keyless run … -v` or `Browser.Debug`): writes `.agent/logs/diagnostics.log` and saves screenshots on errors.

### Limits

See [agentscript-spec.md](agentscript-spec.md#limits-enforced-injected-into-the-system-prompt).

### Snapshot

- Filtering applies to the **attached copy only** — `tree-view-result.txt` and `code-dump-result.md` on disk are never modified.
- Oversized dumps are split at `### heading` boundaries into `code-dump-result-part1.md`, `-part2.md`, … and uploaded as consecutive files.

### Git

Protected patterns are matched case-insensitively with `*` wildcards. The guard runs **in code** before every file-changing command and every commit/push.

### Commands

The allow list matches the first program name of the command line. The built-in deny list (format/shutdown/force-push/`git config --global`/`curl | sh`/recursive deletes/encoded PowerShell/…) always applies and cannot be configured away.

## Other files

- `selectors.json` — the editable chat selector catalog (see [browser-automation.md](browser-automation.md)); created with defaults on first run.
- `.agent/session.json` — the saved chat URL, branch and turn counter (powers `keyless resume`).
- `.agent/backups/` — timestamped backups of every changed file (powers `keyless undo`).
- `.agent/logs/turn-N.jsonl` — full per-turn transcript (messages, replies, commands, results).
- `.git/info/exclude` — the agent adds `.agent/`, `tree-view-result.txt` and `code-dump-result.md` automatically.
