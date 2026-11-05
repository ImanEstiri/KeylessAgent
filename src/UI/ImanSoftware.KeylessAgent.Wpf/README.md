# KeylessAgent WPF

**Desktop front end for KeylessAgent — a coding agent that runs on your own DeepSeek login. No API key.**

The WPF app wraps the same engine as the [CLI](../ImanSoftware.KeylessAgent.Cli/README.md) in a Midnight & Champagne glass UI: pick a workspace, watch the turn stream live (commands, diffs, results), and control the hidden Chrome window without touching a terminal.

> Automating a web UI may violate the provider's terms of use and can get your account limited. This project is not affiliated with DeepSeek.

## Highlights

- **Agent page** — request box, live turn feed (thinking, commands, diffs, results), pacing display, cancel support. Every file a turn touches is backed up and shown as a diff card.
- **Doctor page** — Start Chrome, Show Chrome, Hide Chrome, Close Chrome buttons plus the live selector check (MATCHED / NOT FOUND / NOT APPLICABLE per selector) with the same diagnostics as `keyless doctor`.
- **Settings page** — edit `keyless.json` (approval mode, pacing, browser behavior, snapshot budgets, filters) with validation; changes are written back through the same tolerant loader the CLI uses.
- **Workspace page** — pick the workspace, inspect the environment (git, dotnet, node…), switch branches safely.
- **Undo** — restore the newest backup batch from `.agent/backups`.

## Chrome window control

The agent's Chrome window is invisible by design (a tiny corner square that keeps the renderer unthrottled). The Doctor page gives you:

| Button | What it does |
|---|---|
| Start Chrome | Starts/attaches the dedicated profile and shows the window (one-time DeepSeek login) |
| Show Chrome | Brings the window to (100,100) 1280×900 whenever you need it |
| Hide Chrome | Returns it to the imperceptible corner square |
| Close Chrome | Gracefully closes the whole browser (CDP `Browser.close`, then a process-tree kill fallback) — refused for a Chrome you launched yourself |

On normal application exit the app closes the Chrome it launched automatically (bounded to a few seconds; a Chrome you attached to manually is never touched).

## Requirements

- Windows 10/11, .NET 10 Desktop Runtime, Git, Chrome.
- Run `keyless chrome` (or Start Chrome) once and log in to DeepSeek in the dedicated profile.

## Build

```bash
dotnet build src/UI/ImanSoftware.KeylessAgent.Wpf -c Release
```

## License

MIT — see [LICENSE](https://github.com/ImanEstiri/KeylessAgent/blob/main/LICENSE.txt).

Full documentation — AgentScript, configuration, selectors, reply sources, security model — lives in the main repository: **[github.com/ImanEstiri/KeylessAgent](https://github.com/ImanEstiri/KeylessAgent)**.
