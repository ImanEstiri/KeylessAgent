# Git workflow

## Session start (per workspace)

1. **Repository check** — not a git repo → the turn stops with `KA_NOT_A_GIT_REPOSITORY`.
2. **Exclude upkeep** — `.agent/`, `tree-view-result.txt`, `code-dump-result.md` are appended to `.git/info/exclude` when missing.
3. **Dirty tree** — if there are uncommitted changes the user chooses: `continue` (work on top), `stash` (`git stash push -u -m "keyless-agent auto stash"`), or `cancel`.
4. **Main branch detection** — `git symbolic-ref refs/remotes/origin/HEAD`, falling back to `origin/main`, then `origin/master`, then `main`.
5. **Branch setup**
   - Current branch protected (`main`/`master`/`release*`, case-insensitive, `*` wildcards, configurable) → the agent creates `agent/<yyyyMMdd-HHmm>-<slug>` from it and switches (`git checkout -b`). The slug comes from the request (kebab-case, `[a-z0-9-]`, max 40 chars, `task` when nothing survives — non-Latin requests included).
   - Current branch not protected → the user chooses: press Enter for a new agent branch (default) or `keep` to continue on it.

## During the turn

- The **branch guard runs in code** before every `newfile`, `updatefile`, `editfile`, `deletefile`, and every `commit`/push — a protected branch can never be modified even if the model asks.
- `commit` = `git add -A` → `git commit -m "<message>"` → `git push -u origin <branch>` (never force). A failed push is returned to the model as an error result and shown to the user.

## Hard never-list (enforced by the policy / git service)

No force push, no `reset --hard` on shared branches, no branch deletion, no merge, no rebase onto protected branches, no git global/system config changes. **Merging the agent branch is done only by the developer.**

## End of turn

- The branch name and the commit list (`git log --oneline`, newest first) are shown to the user.
- The session (chat URL + branch + turn number) is persisted in `.agent/session.json`, so later requests (and `keyless resume`) continue in the same chat on the same branch.

## Undo

Every changed file is backed up to `.agent/backups/<timestamp>/…` before modification. `keyless undo` restores the newest batch — it does **not** touch commits; use git for that.
