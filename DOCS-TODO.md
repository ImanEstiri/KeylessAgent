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

## Task 2 additions (final)
- AgentLoop: BatchFailureMode logic — ReadOnly failures (command_not_found) never stop; Build/Execute/Risky/file-command/commit failures stop remaining non-say commands with "an earlier command failed (#N)"; StopOnAnyFailure stops on any; Continue never stops.
- on_error=continue|stop option on @@runcommand parsed and validated (KA_PARSE_BAD_OPTION for unknown values); RunCommandCommand.OnError property.
- Control directives: @@next (NextCommand + NextExecutor) and @@done — exactly one per reply; conflicting → KA_PARSE_CONFLICTING_CONTROL; say-only → KA_PARSE_SAY_ONLY.
- @@movefile <from> + <<<TO ... TO>>> (MoveFileCommand + MoveFileExecutor): sandboxed both paths, backup, refuses overwrite (KA_FILE_ALREADY_EXISTS), creates parents, undo-covered.
- EnvironmentProbe: parallel 3s-timeout probes (OS, git, dotnet, node, npm, python) → [ENVIRONMENT] block in the first message + one-line summary.
- Browser: MinDelayMs=0, MaxDelayMs=150, HumanDelay=false defaults; random delay only when HumanDelay=true.

## Task 3 additions
- Lenient one-line payload: <<<MSG text MSG>>> / <<<FIND text FIND>>> / etc for non-CONTENT sections, normalized to multi-line form (CONTENT never affected).
- Per-message reminder: ~8-line AgentScript summary + env summary prepended to every user-request message; full system prompt stays on first message only.
- ReplyExtractor: entity-decoded text in the fake (HtmlEncode in fake page), one-line payload tests with real failing reply.

## Task 4 additions
- Browser.WindowMode (Minimized default) + Browser.MinimizeAfterLogin (true) config keys.
- Launch flags: --disable-renderer-backgrounding --disable-background-timer-throttling --disable-backgrounding-occluded-windows (prevents throttling when minimized).
- Hover calls removed from copy capture (never un-minimizes during normal operation).
- Window restored ONLY for login/captcha; minimized again after (Browser.MinimizeAfterLogin).
- Delay defaults changed: MinDelayMs=0, MaxDelayMs=150, HumanDelay=false.

## Task 5 additions (partial)
- Raw reply panel shows ONLY when the reply is invalid or with -v; valid replies get a compact summary "Reply: N commands (kinds) via Copy/Dom".
- script declaration moved before the panel so both the valid and invalid paths can access it.

## Task 6 additions
- Browser.MaxTurnsPerChat (default 20): when session.TurnsInChat >= max, session cleared and a fresh chat started.
- Selector key ChatLimitNotice (3 fallbacks) for conversation-too-long detection.
- Session model: TurnsInChat counter.
