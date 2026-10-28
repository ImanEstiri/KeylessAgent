# DOCS-TODO (temporary)

## Prompt-lessons batch
- system-prompt.md: "you DO run commands" + payload markers alone on their own lines + no ./ prefix + rules apply to the whole conversation.
- format-correction.txt: same lessons + cheat-sheet.
- AgentLoop: system prompt sent as message 1 alone; request+attachments as message 2.
- Console trail: raw reply panel (2000-char cap) + validating verdict with last source.
- Upload: hidden file input — SetInputFiles tried directly per fallback (no Visible wait).
- Provider: chat tab stays open after turns/exit unless CloseChromeOnExit; auto-minimize after login (CDP setWindowBounds); ResumeChatUrl navigation when the reused tab isn't on the saved chat.
- CommitExecutor/file executors: NoGitMode in AgentExecutionContext — branch guard skipped, @@commit → Error/KA_GIT_NOT_INSTALLED "git unavailable, nothing committed" without failing the turn.
- Dirty menu: "carry" option dropped when already on an agent branch (default becomes "here"); menu skipped entirely when all dirty paths ∈ session.AgentChangedPaths AND on agent branch; uncommitted-changes notice distinguishes agent vs foreign changes.
- AgentLoop: capabilities probed once via IWorkspaceInspector (cached); turn change baseline via IChangeTracker; agent-changed paths persisted to session.AgentChangedPaths after each batch.
- Session model: AgentChangedPaths + TurnsInChat added.

## Task 2 additions (partial — classifier + reasons done; @@next/@@movefile/env pending)
- ICommandRiskClassifier + CommandRiskTier (ReadOnly/Build/Execute/Risky/Denied); per-segment classification; unknown programs default Risky.
- Commands.AutoApprove (default ["ReadOnly","Build"]) + Commands.BatchFailureMode (StopOnMutationFailure default).
- CommandExecutionResult.FailureReason (command_not_found/timeout/denied/declined/non_zero_exit/killed); command_not_found shown as dim note.
- RunCommandExecutor: tier-based approval policy per mode; Ask auto-approves configured tiers; confirmation shows tier + reason.
- Error codes: KA_COMMAND_NOT_FOUND, KA_PARSE_BAD_OPTION, KA_PARSE_CONFLICTING_CONTROL, KA_PARSE_SAY_ONLY.
