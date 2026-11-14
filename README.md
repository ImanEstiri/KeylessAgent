# KeylessAgent

**A coding agent that runs on your own DeepSeek login. No API key.**

[![NuGet](https://img.shields.io/nuget/v/ImanSoftware.KeylessAgent.Cli.svg)](https://www.nuget.org/packages/ImanSoftware.KeylessAgent.Cli)
[![Downloads](https://img.shields.io/nuget/dt/ImanSoftware.KeylessAgent.Cli.svg)](https://www.nuget.org/packages/ImanSoftware.KeylessAgent.Cli)
[![License: MIT](https://img.shields.io/github/license/ImanEstiri/KeylessAgent.svg)](LICENSE.txt)

KeylessAgent is a .NET coding agent that does not use an API key. It rides on the DeepSeek web chat **you are logged into** in a dedicated Chrome profile (one manual login, then it persists): it opens a new chat, sends your request together with your project's tree and code dump, waits for the reply, and applies it to your workspace as a strict command language (AgentScript) — on a protected agent branch, never on `main`.

## ⚠️ Terms-of-service and account-risk notice (read this first)

KeylessAgent automates a web UI. **This may violate DeepSeek's Terms of Use and can get your account limited, throttled or banned.** The selectors that drive the page can break with any site update. This project is **not affiliated with DeepSeek** — the name is used only to describe compatibility. Use it at your own risk, on an account you accept losing.

## What and why

Every LLM coding tool wants an API key. If you already use DeepSeek's web chat, KeylessAgent turns that login into your agent backend:

- **No API key, no billing setup.** The browser session you already have is the credential.
- **Strict command language.** The model's reply must parse as [AgentScript v1](docs/agentscript-spec.md); anything else is rejected and re-requested automatically. Nothing outside the grammar ever executes.
- **Workspace-aware.** Every request carries `tree-view-result.txt` and `code-dump-result.md` (generated natively in C# — no scripts to install), filtered for noise and chunked to fit.
- **Loop with feedback.** After each executed batch the agent sends per-command results (`[KEYLESSAGENT RESULTS]`) back to the same chat, so multi-step tasks iterate until `@@done`.
- **Reply sources.** Replies are read via the message's own Copy button (raw Markdown, perfectly preserved) with a DOM extraction fallback. Risk-tier command approval (ReadOnly/Build auto-approve, Risky always confirmed). Research loop with @@next/@@done. See [docs/browser-automation.md](docs/browser-automation.md).
- **Safety in code, not in the prompt.** Protected-branch guard, path sandbox, command policy, backups, approval modes — all enforced in C#, covered by tests.

## Requirements

- Windows (the snapshot analyzer and shell commands assume Windows; see [known limitations](#known-limitations))
- [.NET 10 SDK / runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git (`git-scm.com`) — auto-detected; if the workspace is not a repository yet, KeylessAgent initializes one automatically (`Git.AutoInit`). Without git, a no-git mode runs file commands with backups only
- Chrome. KeylessAgent uses a **dedicated Chrome profile** (`%LOCALAPPDATA%\KeylessAgent\chrome-profile`):
  since Chrome 136 the debug port is ignored for Chrome's default profile, so a daily-profile
  attach is impossible. Run `keyless chrome <workspace>` once and log in to DeepSeek in that
  window — the login persists, and KeylessAgent auto-starts this profile afterwards
  (`Browser.AutoLaunchChrome`).
- A DeepSeek account logged in to [chat.deepseek.com](https://chat.deepseek.com) in that profile

## Installation

KeylessAgent is a .NET global tool:

```bash
dotnet tool install --global ImanSoftware.KeylessAgent.Cli
```

Verify:

```bash
keyless --help
```

## Usage

The workspace path is always the **first input** — the agent never silently assumes the current directory. Pass it as the first argument, or the CLI asks for it (validated, current directory only as the shown default) as the very first prompt:

```bash
keyless run "G:\Projects\MyApp" "Add xUnit tests for the markdown parser"
```

Or start an interactive session — the first prompt is the workspace path, then one request per turn (each finished turn reuses the same chat):

```bash
keyless
keyless "G:\Projects\MyApp"
```

Sample turn output (numbered menus instead of typed answers):

```text
────────────────────────────────────────────── KeylessAgent
Workspace: G:\Projects\MyApp
? The working tree has uncommitted changes. What should I do?
> 1) Carry my uncommitted changes to a new agent branch (default)
  2) Stash my changes first (git stash, restore later yourself)
  3) Continue on the current branch ('main') with my changes in place
  4) Cancel
…spinner while waiting for the reply…
╭─ DeepSeek says ───────────────────────────────╮
│ I will add the tests now.                     │
╰───────────────────────────────────────────────╯
✓ Turn 1 finished on branch agent/20260101-0930-add-markdown-tests
Commits:
  • 3f2a9b1 Add MarkdownParserTests
```

When the workspace is not a git repository yet, the agent initializes one (main branch, .NET `.gitignore`, initial commit) and tells you in one line — no questions asked (`Git.AutoInit`).

Other commands (all workspace-first):

```bash
keyless resume "G:\Projects\MyApp" "continue with the failing test"   # next request in the saved chat
keyless undo "G:\Projects\MyApp"                                      # restore the newest .agent/backups batch
keyless config "G:\Projects\MyApp"                                    # show / create keyless.json
keyless chrome "G:\Projects\MyApp"                                    # start the dedicated Chrome (one-time login)
keyless doctor "G:\Projects\MyApp"                                    # live selector check, never sends anything
keyless run "G:\Projects\MyApp" "..." --dry-run                       # print what would happen, execute nothing
keyless run "G:\Projects\MyApp" "..." --verbose                     # (-v) write .agent/logs/diagnostics.log
keyless run "G:\Projects\MyApp" "..." --timing                        # print the per-phase timing table after the turn
keyless run "G:\Projects\MyApp" "..." --pace Step                    # one command per response (Step | Balanced | Batch)
keyless run "G:\Projects\MyApp" "..." --refresh-env                  # re-probe git/dotnet/python/node instead of the 24 h cache
```

The interactive session understands `/workspace <path>` (switch workspaces), `/newchat` (fresh chat, same workspace), `/tools` / `/refreshenv` (show / re-probe the installed tools) and `/help`.

**Exit codes** for `keyless run` / `keyless resume`:

| Code | Meaning |
| --- | --- |
| 0 | The turn completed. |
| 1 | The turn failed (`result.ErrorMessage`). |
| 2 | The turn stopped early (`result.StoppedReason` is printed, e.g. no reply, chat exhausted). |
| 130 | Cancelled by the user (Ctrl+C during the turn). |

**Ctrl+C** is safe everywhere: during a turn it cancels only that turn (a second press within 2 seconds exits the session); at the idle `keyless>` prompt it exits gracefully with code 0 — logs are flushed and the browser connector is disposed.

The first time a command runs in a workspace it creates `keyless.json` (configuration) and `selectors.json` (editable chat selectors) — see [docs/configuration.md](docs/configuration.md).

## Desktop app (WPF)

KeylessAgent ships a WPF desktop UI ([docs/wpf-ui.md](docs/wpf-ui.md)) built on the same Core
loop — no API key, no second brain: everything the CLI does, in windows instead of a console.

- **Agent page** — activity feed (progress steps, model messages, commands), a multiline composer with dry-run toggle, Send/Stop (Ctrl+Enter / Esc), a changes panel with a collapsible unified diff viewer (CRLF-safe, large-file fast path), the turn summary card and one-click undo of the newest backup batch (with a confirmation dialog).
- **Workspace page** — folder picker, recent workspaces (max 10, persisted), and git capability inspection (git installed? repository? branch? agent branch?).
- **Doctor page** — the live selector check from `keyless doctor` with status chips and a Start-Chrome button; the connection kind shows up in the status bar.
- **Settings page** — the workspace's `keyless.json` read-only (grouped), open/reload buttons, plus UI preferences: Reduce effects, backdrop (Auto/Mica/Acrylic/None), dry run by default.
- **About page** — version, description and the terms-of-service notice.
- **Shell** — Windows 11 Mica/Acrylic backdrop with an app-drawn fallback, custom title bar, queued modal dialogs (one at a time, cancelled when you stop a turn), Persian/Arabic composer direction detection, window size/position persistence with off-screen protection, and a crash log at `%LOCALAPPDATA%\KeylessAgent\wpf-crash.log`.

```bash
dotnet run --project src/UI/ImanSoftware.KeylessAgent.Wpf
```

Requirements: Windows plus the .NET 10 SDK (to run from source); the published app needs the .NET 10 **Desktop Runtime**. On Windows 11 22H2+ the window uses the system Mica/Acrylic backdrop (Auto/Mica/Acrylic/None selectable in Settings); older systems get an app-drawn gradient.

Publish:

```powershell
powershell -File src\UI\ImanSoftware.KeylessAgent.Wpf\publish-wpf.ps1
```

This publishes into `artifacts\wpf` (framework-dependent, win-x64), verifies the Playwright driver files are present, and reports a summary. Screenshot placeholders: *(to be added)*.

> Verification status: the WPF app is covered by 201 automated tests and a warning-free build, and the publish flow was executed successfully . visual feedback is very welcome.

## How it works

```mermaid
flowchart TD
    A["keyless run '<workspace>' '<request>'"] --> B[Load keyless.json]
    B --> C{Git preconditions}
    C -->|not a repo| I[Auto init: git init, .gitignore, initial commit]
    C -->|dirty tree| D{{Numbered menu: carry / stash / here / cancel}}
    C -->|clean, non-protected| E{{Numbered menu: new agent branch / stay / cancel}}
    C -->|protected branch| F[Create agent branch automatically]
    D --> F
    E --> F
    I --> G[Generate tree + code dump natively in C# if stale]
    F --> G
    G --> H[Attach to the dedicated Chrome; open or reuse the DeepSeek tab]
    H --> I2[Send system prompt first time + request + filtered attachments]
    I2 --> J[Wait for the final answer — Copy button first, validated; DOM fallback]
    J --> K{Valid AgentScript v1?}
    K -->|no| L[Send FORMAT ERROR correction] --> J
    K -->|yes| M[Execute say + up to N commands — skipped ones reported]
    M --> N{@@done received?}
    N -->|no| O[Send KEYLESSAGENT RESULTS per command number/name/target] --> J
    N -->|yes| P[Regenerate the snapshots natively]
    L -.retries exhausted.-> P
    O -.iteration limit.-> P
```

The full loop, limits and error codes are documented in [docs/agentscript-spec.md](docs/agentscript-spec.md).

## AgentScript in 30 seconds

The model answers in blocks; anything else is rejected:

```text
@@say
<<<MSG
Adding the file now.
MSG>>>
@@end
@@newfile src/Foo.cs
<<<CONTENT
class Foo {}
CONTENT>>>
@@end
@@done
<<<MSG
Created src/Foo.cs.
MSG>>>
@@end
@@eof
```

Commands: `say`, `newfile`, `updatefile`, `editfile` (FIND/REPLACE, must match exactly once), `mkdir`, `movefile`, `deletefile`, `readfile`, `runcommand`, `ask`, `commit`, `next`, `done`. Everything the model writes is English; file content may be any language. No outer Markdown fence.

Successful file commands carry a `verified:` line (path, bytes, lines, SHA-8, first/last snippets) in the results — the model can trust the write landed without re-reading the file. See the full grammar, pacing and the error-code catalog in [docs/agentscript-spec.md](docs/agentscript-spec.md).

## Reply sources

| Source | How it reads | When |
| --- | --- | --- |
| **Copy** (first in `ReplySource: Auto`) | Hovers the newest message, clicks its message-level Copy control, captures via clipboard interception (the OS clipboard is never touched); falls back to a real clipboard read with save/restore. The copied raw Markdown is validated against the DOM text before use. | The message Copy control exists. |
| **Dom** (fallback, or `ReplySource: Dom`) | Extracts the final-answer element text from the DOM. | Auto fallback, or explicit choice. |

The reasoning block is never read, logged, or sent anywhere. See [docs/browser-automation.md](docs/browser-automation.md).

## Safety model

| Layer | Enforcement |
| --- | --- |
| Protected branches | `main`, `master`, `release*` blocked in code before any file change, commit or push; on a protected branch the agent branch is created automatically |
| Numbered menus | Dirty-tree and branch decisions are choice menus (arrow keys / digits / Enter / Esc) — no typed answers |
| Path sandbox | workspace-relative only; no `..`, no absolute paths, `.git`/`.agent` blocked, symlink escapes rejected |
| Command policy | deny list (format, shutdown, force-push, `git config --global`, `curl \| sh`, …) plus configurable allow list |
| Approval modes | `Ask` (confirm every runcommand and deletefile), `AutoSafe` (allow-listed runcommands auto, deletefile still asks), `Auto` (everything; deletefile is the only confirmation skipped) — with a one-time "AutoSafe is not a security boundary" warning |
| Backups & undo | every changed file is backed up to `.agent/backups`; `keyless undo` restores the newest batch |
| Dry run | `--dry-run` executes nothing |
| Transparency | full per-turn transcript in `.agent/logs`, diffs before file changes in Ask mode |

Details: [docs/security.md](docs/security.md) and [docs/git-workflow.md](docs/git-workflow.md).

## Tools and missing-tool behavior

At session start the agent probes `git`, `dotnet`, `node`, `npm` and `python` (resolving `python` / `py -3` / `python3`, and flagging WindowsApps store aliases) and prints one line:

```text
Tools: git OK, dotnet OK, python MISSING (https://www.python.org/downloads/), node MISSING (https://nodejs.org), npm MISSING (https://nodejs.org)
```

`runcommand`s whose program the environment lists as missing are **never started**: the result comes back as `KA_TOOL_MISSING` ("…Do not retry it. Use the file commands, or ask the user to install it.") and counts as a normal failed command. The probe is cached for 24 h, re-checked per tool after 1 h, and auto-invalidated when reality disagrees with the cache. `run --refresh-env` and `/refreshenv` re-probe manually.

## Offline and composer reset

Before every send the agent checks `navigator.onLine` plus a cheap probe of the chat origin. Offline: nothing is uploaded or sent — the result is `KA_NETWORK_OFFLINE` ("Check your connection and try again."), optionally after waiting `Browser.WaitForNetworkSeconds` for the connection. A drop during upload, send-confirmation or the reply wait stops the wait at once, cleans the composer, and reports the same code; the chat session stays valid, so you can resend the same request.

Before every message the composer is reset to a clean state: leftover text is cleared and stale attachment chips (failed ones first) are removed, with a page reload as fallback. After a failed or cancelled attempt with attachments, the same cleanup runs best-effort, so the next message never inherits stale chips.

## Pace modes

`Agent.Pace` caps how many non-`say` commands one response may carry — **Step** = 1, **Balanced** = 3 (default), **Batch** = `Limits.MaxCommandsPerResponse`; the effective limit is `min(Pace, MaxCommandsPerResponse)`:

| Pace | Per response | Prompt rule |
| --- | --- | --- |
| `Step` | 1 command | One command, then `@@next`; verify the result (verification summary or `readfile`) before the next step. Iterations raised to ≥ 60. |
| `Balanced` | 3 commands | Group at most 3 related commands, verify after each group. |
| `Batch` | `Limits.MaxCommandsPerResponse` | Unchanged batch behavior. |

Extra commands are skipped with `pace=<Pace>: only N command(s) per response; send the next one after reading this result`. Set it in `keyless.json` (`"Agent": { "Pace": "Step" }`) or per turn with `keyless run … --pace Step`.

## Known limitations

- **Windows only** for now (the snapshot analyzer and process launching assume Windows). The engine itself is portable.
- **Web-UI automation is brittle**: DeepSeek can change its DOM at any time; selectors live in `selectors.json` so they can be fixed without recompiling — run `keyless doctor` after any DeepSeek update. See [docs/browser-automation.md](docs/browser-automation.md).
- The message Copy button, Stop indicator, New Chat button and attachment chips are **not yet verified against a live chat** — doctor reports their state.
- No captcha solving: if a captcha appears, the agent waits for you to solve it in the browser.
- The chat's context limit ends long turns: exhaustion is detected only indirectly (reply timeouts).
- Line endings are preserved per file (dominant ending wins for mixed files); a file with a truly 50/50 mix keeps LF.

## Roadmap

- [ ] Windows Forms desktop UI (Core is UI-agnostic; implement `IAgentUserInterface`)
- [ ] Official-API provider behind `IChatProvider` (no browser)
- [ ] Chat-exhaustion detection and automatic continuation in a new chat
- [ ] `TreeAndChanged` snapshot diffing
- [ ] Cross-platform snapshot scripts (PowerShell/shell)

## Repository layout

```text
KeylessAgent/
├── src/
│   ├── Core/
│   │   ├── ImanSoftware.KeylessAgent.Abstractions/   # models, enums, service contracts, IAgentUserInterface
│   │   └── ImanSoftware.KeylessAgent.Core/           # parser, executors, git, snapshot, browser, agent loop, factory
│   ├── UI/
│   │   ├── ImanSoftware.KeylessAgent.Cli/            # the `keyless` dotnet tool (Spectre.Console.Cli)
│   │   └── ImanSoftware.KeylessAgent.Wpf/            # WPF desktop UI (MVVM) + publish-wpf.ps1
│   ├── Playground/                                   # manual-testing console app (fake chat provider)
│   └── tests/                                        # xunit + FluentAssertions (733 tests: Core/CLI + WPF)
├── docs/                                             # architecture, AgentScript spec, configuration, wpf-ui, …
├── Directory.Build.props / Directory.Packages.props  # shared properties + Central Package Management
└── publish.ps1                                       # packs the CLI tool into artifacts/
```

## Building from source

The application version lives in **one place**: `<Version>` in [Directory.Build.props](Directory.Build.props). `Program.cs` reports it from the built assembly, and `publish.ps1` reads and bumps it there — the CLI csproj carries no `<Version>` of its own.

`publish.ps1` packs and pushes to NuGet.org. `-SkipPush` packs only (and auto-bumps the patch version if the artifact already exists) — it does **not** require `NUGET_API_KEY`, which is only checked right before an actual push.

```bash
git clone https://github.com/ImanEstiri/KeylessAgent.git
cd KeylessAgent
dotnet build
dotnet test
```

Run the CLI from source (no browser needed for the fake-provider harness):

```bash
dotnet run --project src/Playground/ImanSoftware.KeylessAgent.Playground
dotnet run --project src/UI/ImanSoftware.KeylessAgent.Cli -- run "G:\Projects\MyApp" "Add a README section"
```

## Contributing

Contributions are welcome — bug reports, selector fixes, ideas, and pull requests alike.

- **Found a bug?** Open an [issue](https://github.com/ImanEstiri/KeylessAgent/issues) with the turn log from `.agent/logs` (cookies and tokens are never logged). For selector problems, attach a `keyless doctor --capture` snapshot — personal text is masked automatically.
- **Want to code?** The pipeline is small services (`AgentScriptParser`, `ResponseFormatGuard`, `WorkspaceCommandExecutor`, `GitService`, `SnapshotService`, `BrowserConnector`, `AgentLoop`); most changes touch one file. Keep `dotnet build` warning-free and `dotnet test` green.

## License

MIT — see [LICENSE.txt](LICENSE.txt).
