# Git workflow

## Session start (per workspace)

1. **Repository check** — not a git repo:
   - `Git.AutoInit` true (default): the agent checks that git is installed (`KA_GIT_NOT_INSTALLED` with a download link when missing), runs `git init -b main` (fallback `git init` + `git branch -M main` on old git), creates a .NET `.gitignore` when the workspace contains a `.sln`/`.slnx`/`.csproj` (`dotnet new gitignore`, else a built-in minimal template), makes the initial commit "Initial commit (created by KeylessAgent)" — with a one-shot `-c user.name="KeylessAgent" -c user.email="keyless@localhost"` identity when none is configured (git config is never changed) — and tells the user in one line. Failure → `KA_GIT_INIT_FAILED`.
   - `Git.AutoInit` false: the turn stops with `KA_NOT_A_GIT_REPOSITORY`.
2. **Exclude upkeep** — `.agent/`, `tree-view-result.txt`, `code-dump-result.md` are appended to `.git/info/exclude` when missing.
3. **Dirty tree (numbered menu, no typing)** — options: **carry** (default; changes move to the new agent branch), **stash** (`git stash push -u -m "keyless-agent auto stash"`; restore later yourself), **here** (continue on the current branch — only offered on non-protected branches), **cancel**. Arrow keys / digits / Enter / Esc.
4. **Main branch detection** — `git symbolic-ref refs/remotes/origin/HEAD`, falling back to `origin/main`, then `origin/master`, then `main`.
5. **Branch setup**
   - Current branch protected (`main`/`master`/`release*`, case-insensitive, `*` wildcards, configurable) → **no menu**: the agent creates `agent/<yyyyMMdd-HHmm>-<slug>` automatically and tells the user. The slug comes from the request (kebab-case, `[a-z0-9-]`, max 40 chars, `task` when nothing survives — non-Latin requests included).
   - Current branch not protected AND the tree is clean → one menu: **new** (default; create a new agent branch) / **stay** (continue on the current branch) / **cancel**. When the tree was dirty, the dirty menu already decided (carry → new agent branch, here → stay) and no second menu appears.

At most **one menu** per turn; none when the tree is clean and the branch is protected.

## During the turn

- The **branch guard runs in code** before every `newfile`, `updatefile`, `editfile`, `deletefile`, and every `commit`/push — a protected branch can never be modified even if the model asks.
- `commit` = `git add -A` → `git commit -m "<message>"` → push. When the repo has **no `origin` remote**, the commit is made locally and the push is SKIPPED with a notice ("No 'origin' remote; committed locally") — the result sent to the model says so. When origin exists and the push fails, the failure is returned to the model as an error result and shown to the user.

## Hard never-list (enforced by the policy / git service)

No force push, no `reset --hard` on shared branches, no branch deletion, no merge, no rebase onto protected branches, no git global/system config changes. **Merging the agent branch is done only by the developer.**

## End of turn

- The branch name and the commit list (`git log --oneline`, newest first) are shown to the user.
- The session (chat URL + branch + turn number) is persisted in `.agent/session.json`, so later requests (and `keyless resume`) continue in the same chat on the same branch.

## Undo

Every changed file is backed up to `.agent/backups/<timestamp>/…` before modification. `keyless undo` restores the newest batch — it does **not** touch commits; use git for that.
