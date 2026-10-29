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

Inside the interactive session: `/workspace <path>` switches workspaces, `/newchat` starts a fresh chat in the same workspace, `/help` lists slash commands, any other `/`-input is rejected with a hint.

**Ctrl+C** is safe: during a turn it cancels only that turn (press again within 2 seconds to exit); at the `keyless>` prompt it exits gracefully. `keyless run` accepts `--timing` to print the per-phase timing table after the turn.

Exit codes for `keyless run`: **0** completed, **1** failed, **2** stopped early (reason printed), **130** cancelled by the user.

First run in a non-git workspace initializes the repository automatically (main branch, .NET .gitignore, initial commit — `Git.AutoInit`). Without git installed, a no-git mode runs file commands with backups only (no branch protection, no commits). Run `keyless chrome` once and log in to DeepSeek in the dedicated profile; since Chrome 136 the debug port only works with a dedicated profile, and the login persists there. Git decisions (dirty tree, branch choice) are presented as numbered menus — arrows, digits, Enter, Esc; no typed answers. Command risk tiers (ReadOnly/Build/Execute/Risky) control what runs automatically; risky commands always ask.

Full documentation — AgentScript, configuration, selectors, reply sources, security model — lives in the main repository:

**[github.com/ImanEstiri/KeylessAgent](https://github.com/ImanEstiri/KeylessAgent)**

## License

MIT — see [LICENSE](https://github.com/ImanEstiri/KeylessAgent/blob/main/LICENSE.txt).
