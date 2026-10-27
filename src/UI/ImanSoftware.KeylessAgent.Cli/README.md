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

Inside the interactive session: `/workspace <path>` switches workspaces, `/help` lists slash commands, any other `/`-input is rejected with a hint.

First run in a non-git workspace initializes the repository automatically (main branch, .NET .gitignore, initial commit — `Git.AutoInit`). Run `keyless chrome` once and log in to DeepSeek in the dedicated profile; since Chrome 136 the debug port only works with a dedicated profile, and the login persists there. Git decisions (dirty tree, branch choice) are presented as numbered menus — arrows, digits, Enter, Esc; no typed answers.

Full documentation — AgentScript, configuration, selectors, reply sources, security model — lives in the main repository:

**[github.com/ImanEstiri/KeylessAgent](https://github.com/ImanEstiri/KeylessAgent)**

## License

MIT — see [LICENSE](https://github.com/ImanEstiri/KeylessAgent/blob/main/LICENSE.txt).
