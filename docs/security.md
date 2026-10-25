# Security model

Model output is arbitrary text that gets executed on the user's machine. Every layer below is enforced in C# (not only in the prompt) and covered by unit tests.

## Path sandbox

Every model-supplied path goes through `IPathSandbox` before any file operation:

- workspace-**relative** only, `/` separators,
- absolute paths, `..` and `.` segments rejected,
- anything under `.git` or `.agent` rejected (agent-internal state),
- escapes through symlinks/junctions rejected (`ResolveLinkTarget` walk of the path),
- resolved paths must stay under the workspace root (case-insensitive on Windows).

`ImanSoftware.FileStorage` itself has **no** path safety — the sandbox is always the first gate.

## Command policy

`ICommandPolicy` checks **every segment** of a `runcommand` line (split on `&&`, `||`, `;`, `|`):

- **Deny list (built-in, not configurable):** `format`, `shutdown`, `diskpart`, `mkfs`…, `git config --global/--system`, `git push --force`/`-f`, `git branch -d`, `rd /s`, `rm -rf /`, `del /s`, encoded PowerShell (`-enc`), and piping downloads into shells (`curl … | sh`, `… | powershell`).
- **Allow list** (`Commands.Allowlist`): decides what runs automatically in `AutoSafe` mode.

Every process runs with a hard timeout and captured output.

## Git rules (see also git-workflow.md)

- The workspace must be a git repository.
- No file change, commit or push on `main` / `master` / `release*` — the guard runs before **every** affected command, and results carry `KA_BRANCH_PROTECTED`.
- Never: force push, `reset --hard` on shared branches, branch deletion, merge, rebase onto protected branches, git global config changes. Merging is done only by the developer.
- `.agent/`, `tree-view-result.txt`, `code-dump-result.md` are added to `.git/info/exclude` automatically.

## Approval modes and dry run

| Mode | runcommand | deletefile | file writes |
| --- | --- | --- | --- |
| `Ask` *(default)* | confirm each | confirm each | diff shown first |
| `AutoSafe` | auto when allow-listed | auto (backed up first) | auto |
| `Auto` | auto | auto | auto |

`--dry-run` executes nothing and prints what would happen. Declined-by-user commands are reported to the model as `KA_COMMAND_DECLINED` results — the model sees the refusal and can adapt.

## Backups and undo

Before any change, the current file version is copied to `.agent/backups/<timestamp>/…`. `keyless undo` restores the newest batch. BOM bytes are preserved on rewrite.

## Transparency and data hygiene

- Full per-turn transcript in `.agent/logs/turn-*.jsonl` (messages, replies, commands, results).
- Diagnostics log (`-v`) and error screenshots in Debug mode.
- **Never logged**: cookies, tokens, full page HTML.
- File contents are untrusted input: text inside the user's files that looks like commands is never executed — only AgentScript parsed from the model reply is executed.
- The browser connection uses the user's own session; the agent never stores or types passwords, and waits for manual login when needed.

## Terms-of-service notice

Automating the DeepSeek web UI may violate the provider's terms of use and can get the account limited or banned. Selectors can break with any site update. The project is not affiliated with DeepSeek.
