# Git workflow

## Session start (per workspace)

1. **Repository check** — probed once per session via IWorkspaceInspector (GitInstalled, IsRepository, CurrentBranch, IsAgentBranch). Not a git repo:
   - `Git.AutoInit` true (default): the agent checks that git is installed (`KA_GIT_NOT_INSTALLED` with a download link when missing), runs `git init -b main` (fallback `git init` + `git branch -M main` on old git), creates a .NET `.gitignore` when the workspace contains a `.sln`/`.slnx`/`.csproj` (`dotnet new gitignore`, else a built-in minimal template), makes the initial commit "Initial commit (created by KeylessAgent)" — with a one-shot `-c user.name="KeylessAgent" -c user.email="keyless@localhost"` identity when none is configured (git config is never changed) — and tells the user in one line. Failure → `KA_GIT_INIT_FAILED`.
   - `Git.AutoInit` false: the turn stops with `KA_NOT_A_GIT_REPOSITORY`.
   - **git not installed at all**: ONE warning ("no branch protection, no commits, no git history, changes cannot be reverted with git; file changes are still backed up in .agent/backups"), then NO-GIT MODE: no branch guard, no agent branch, no dirty menu, no commit/push. @@commit returns Error/KA_GIT_NOT_INSTALLED "git unavailable, nothing committed" without failing the turn. The system prompt carries a note telling the model not to use @@commit or git commands.
2. **Exclude upkeep** — `.agent/`, `tree-view-result.txt`, `code-dump-result.md` are appended to `.git/info/exclude` when missing.
3. **Dirty tree (numbered menu, no typing)** — skipped when every dirty path was changed by the agent itself (tracked in session.AgentChangedPaths) AND the current branch is an agent branch. Options: **carry** (changes move to the new agent branch; only offered when NOT already on an agent branch), **stash** (`git stash push -u -m "keyless-agent auto stash"`; restore later yourself), **here** (continue on the current branch — only on non-protected branches), **cancel**. Default: carry off-branch, here on-branch. Arrow keys / digits / Enter / Esc.

Change tracking works with and without git: with git, `git status --porcelain` snapshots are compared; without git, a lightweight path/size/mtime index (excluding bin/obj/.git/.agent/node_modules/artifacts/.vs/.idea/TestResults) is used. The agent-changed path set is persisted in `.agent/session.json` after each batch.
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
