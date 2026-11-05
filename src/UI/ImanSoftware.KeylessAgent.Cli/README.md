# KeylessAgent CLI

**A coding agent that runs on your own DeepSeek login. No API key.**

`keyless` is the command-line front end for [KeylessAgent](https://github.com/ImanEstiri/KeylessAgent). It drives your logged-in DeepSeek web chat through a dedicated Chrome profile, applies the model's replies to your workspace using the strict AgentScript command language, and keeps your work on a protected agent branch.

> Automating a web UI may violate the provider's terms of use and can get your account limited. This project is not affiliated with DeepSeek.

## Installation

Requires the **.NET 10** runtime, Git and Chrome.

```bash
dotnet tool install --global ImanSoftware.KeylessAgent.Cli
```

## Usage

The workspace path is always the first input — pass it as the first argument or answer the first prompt. The agent never silently assumes the current directory.

```bash
keyless run "G:\Projects\MyApp" "Add xUnit tests for the markdown parser"
keyless                              # prompts for the workspace path first; one chat reused
keyless resume "G:\Projects\MyApp" "continue with the failing test"
keyless undo "G:\Projects\MyApp"     # restore the newest backup batch
keyless config "G:\Projects\MyApp"   # show / create keyless.json
keyless chrome "G:\Projects\MyApp"   # start the dedicated Chrome (one-time DeepSeek login)
keyless doctor "G:\Projects\MyApp"   # live selector check, never sends anything
```

Inside the interactive session: `/workspace <path>` switches workspaces, `/newchat` starts a fresh chat in the same workspace, `/tools` shows the installed tools, `/refreshenv` re-probes them, `/help` lists slash commands, any other `/`-input is rejected with a hint.

**Ctrl+C** is safe: during a turn it cancels only that turn (press again within 2 seconds to exit); at the `keyless>` prompt it exits gracefully. `keyless run` accepts `--timing` to print the per-phase timing table after the turn.

Exit codes for `keyless run`: **0** completed, **1** failed, **2** stopped early (reason printed), **130** cancelled by the user.

First run in a non-git workspace initializes the repository automatically (main branch, .NET .gitignore, initial commit — `Git.AutoInit`). Without git installed, a no-git mode runs file commands with backups only (no branch protection, no commits). Run `keyless chrome` once and log in to DeepSeek in the dedicated profile; since Chrome 136 the debug port only works with a dedicated profile, and the login persists there. Git decisions (dirty tree, branch choice) are presented as numbered menus — arrows, digits, Enter, Esc; no typed answers. Command risk tiers (ReadOnly/Build/Execute/Risky) control what runs automatically; risky commands always ask.

Full documentation — AgentScript, configuration, selectors, reply sources, security model — lives in the main repository:

**[github.com/ImanEstiri/KeylessAgent](https://github.com/ImanEstiri/KeylessAgent)**

## Commands

| Command | Purpose |
|---|---|
| `keyless run <workspace> <request>` | Run one agent turn: snapshot → chat → execute → results |
| `keyless resume <workspace> <request>` | Continue the chat saved in `<workspace>/.agent/session.json` |
| `keyless undo <workspace>` | Restore the newest backup batch from `.agent/backups` |
| `keyless config <workspace>` | Show the effective configuration (creates `keyless.json` with defaults when missing) |
| `keyless chrome <workspace>` | Start/attach the dedicated Chrome profile and show its window (for the one-time DeepSeek login) |
| `keyless doctor <workspace>` | Live selector check against the running Chrome — never sends anything |
| `keyless show <workspace>` | Bring the hidden Chrome window into view (pure CDP move; never disturbs a running turn) |
| `keyless hide <workspace>` | Move the Chrome window back to its hidden corner |
| `keyless diagnostics chrome <workspace>` | Print the running Chrome's command line and verify the anti-throttling flags |
| `keyless selectors refresh <workspace>` | Force-merge `selectors.json` with the current embedded defaults (backup written) |

`run`, `resume`, `undo`, `config`, `chrome`, `doctor`, `show`, `hide` prompt for the workspace path when omitted.

### keyless run

```bash
keyless run "G:\Projects\MyApp" "Add xUnit tests for the markdown parser"
keyless run "G:\Projects\MyApp" "Fix the failing parser test" --dry-run   # commands are parsed, never executed
keyless run "G:\Projects\MyApp" "Refactor the config loader" -v          # verbose progress
keyless run "G:\Projects\MyApp" "Add a README section" --timing          # per-phase timing table after the turn
keyless run "G:\Projects\MyApp" "Fix the tests" --pace Step             # 1 command per response (Balanced = 3, Batch = limit)
keyless run                                                                # prompts for everything
```

Exit codes: **0** completed, **1** failed, **2** stopped early (reason printed), **130** cancelled by the user.

### keyless resume

Sends the next request to the SAME DeepSeek chat saved in the workspace session (conversation context preserved):

```bash
keyless resume "G:\Projects\MyApp" "continue with the failing test"
```

### keyless undo

Restores the newest backup batch (every file a turn touched is backed up before mutation):

```bash
keyless undo "G:\Projects\MyApp"
```

### keyless config

```bash
keyless config "G:\Projects\MyApp"   # prints the effective configuration as a table
```

The first run writes the full default `keyless.json` (edit it freely — parsing is tolerant: comments, trailing commas, case-insensitive keys).

### keyless chrome

Starts the dedicated Chrome profile with the debug port (mandatory since Chrome 136) and SHOWS the window so you can log in once:

```bash
keyless chrome "G:\Projects\MyApp"
```

The login persists in the profile; afterwards runs keep the window hidden (TinyCorner) and reuse the login.

### keyless doctor

Races every selector against the live page and prints a MATCHED / NOT FOUND / NOT APPLICABLE table:

```bash
keyless doctor "G:\Projects\MyApp"
keyless doctor "G:\Projects\MyApp" --capture ".\doctor-capture"   # trimmed HTML snapshots for selector fixing
keyless doctor "G:\Projects\MyApp" --watch                        # capture while you send a prompt manually
```

### keyless show / keyless hide

```bash
keyless show "G:\Projects\MyApp"             # window appears at (100,100) 1280x900
keyless show "G:\Projects\MyApp" --peek 3    # visible for 3 seconds, then back offscreen
keyless hide "G:\Projects\MyApp"             # back to the tiny corner square
```

### keyless diagnostics chrome

```bash
keyless diagnostics chrome "G:\Projects\MyApp"   # prints chrome.exe command lines + flag verdict
```

### keyless selectors refresh

```bash
keyless selectors refresh "G:\Projects\MyApp"   # force-merge selectors.json with the embedded defaults
```

Never-customized keys are refreshed to the current embedded selectors, hand-edited keys survive, and the previous file is backed up as `selectors.json.v<N>.bak`.

### Interactive session

`keyless` without arguments starts the interactive prompt (`keyless>`):

```text
/workspace G:\Projects\OtherApp   switch workspace
/newchat                            fresh DeepSeek chat in the same workspace
/tools                              show installed tools (git, dotnet, …)
/refreshenv                         re-probe the environment
/help                               list all slash commands
```

**Ctrl+C** is safe: during a turn it cancels only that turn (press again within 2 seconds to exit); at the `keyless>` prompt it exits gracefully.

## Versioning and publishing

The package version lives in ONE place: `<Version>` in `Directory.Build.props` (1.0.0) — the CLI assembly reads it from there and the consistency test forbids a second copy. Publishing mirrors the DepLens flow:

1. `publish.bat` in this folder runs `publish.ps1` from the solution root.
2. The script packs the CLI (`dotnet pack -c Release`) into `artifacts/ImanSoftware.KeylessAgent.Cli`, **auto-bumping the patch version** in `Directory.Build.props` whenever an artifact for the current version already exists.
3. It then pushes the `.nupkg` (plus the `.snupkg` symbols) to NuGet.org with a 30 s timeout; on a failed/timed-out push the freshly built artifacts are removed so the version is not wasted.

Prerequisites: `NUGET_API_KEY` set once outside of git (`setx NUGET_API_KEY your-key`, then restart the terminal). `-SkipPush` packs only. Install after publishing:

```bash
dotnet tool install --global ImanSoftware.KeylessAgent.Cli
dotnet tool update --global ImanSoftware.KeylessAgent.Cli
```

## License

MIT — see [LICENSE](https://github.com/ImanEstiri/KeylessAgent/blob/main/LICENSE.txt).

## Pacing and tools

`keyless run … --pace Step|Balanced|Batch` (and `--pace` on the interactive command) caps how many non-`say` commands one response may carry: Step = 1, Balanced = 3, Batch = `Limits.MaxCommandsPerResponse`. Skipped commands are reported with `pace=<Pace>: only N command(s) per response; send the next one after reading this result`.

Each session prints one tools line (`Tools: git OK, dotnet MISSING (https://…), …`). `runcommand`s whose tool is missing are never started (`KA_TOOL_MISSING`) — use the file commands or install the tool. `--refresh-env` re-probes the environment instead of using the 24 h cache.