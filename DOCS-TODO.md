# DOCS-TODO (temporary; deleted in Task 3)

## Task 1 — Git workflow UX
- IAgentUserInterface.ChooseAsync(ChoiceRequest) — UI-agnostic numbered choice prompt; ChoiceRequest/ChoiceOption/ChoiceResult in Abstractions/Models/Agent.
- CLI ConsoleUserInterface.ChooseAsync: numbered menu, arrow keys + Enter, digit hotkeys, Esc/Ctrl+C cancel, default highlighted "(default)"; non-interactive input returns the default and prints it.
- AgentLoop git preconditions rewritten as menus: dirty tree = carry/stash/here/cancel (here omitted on protected branch); non-protected clean = new/stay/cancel; protected branch = agent branch auto-created, no menu.
- Old "press Enter or type keep" and "type continue/stash/cancel" prompts removed; AskAsync kept only for free-form (e.g. @@ask).
- ConfirmAsync: Enter accepts the bracketed default, no loop on empty input.
- Auto git init: git missing → KA_GIT_NOT_INSTALLED; git init -b main (fallback init + branch -M main); Git.AutoInit default true; one-line notice.
- Auto .gitignore: dotnet new gitignore when dotnet works, else built-in minimal template (bin/, obj/, .vs/, *.user, .idea/).
- Initial commit "Initial commit (created by KeylessAgent)" with one-shot -c user.name/email identity fallback.
- No 'origin' remote → @@commit commits locally and skips push with notice; error codes KA_GIT_NOT_INSTALLED, KA_GIT_INIT_FAILED; config Git.AutoInit.

## Task 2 — ReplySource (Copy button)
- Browser.ReplySource = Auto | Copy | Dom (default Auto); IReplyReader strategy + validation in Core.
- New selector key MessageCopyButton (hover newest message first; newest message only).
- Capture strategies: init-script interception of clipboard.writeText/execCommand/copy event gated by a capture flag (OS clipboard untouched) → OS clipboard read with grant + save/restore in finally → Dom fallback (Auto) / KA_REPLY_TIMEOUT (Copy).
- Validation: normalized (markdown/punctuation stripped, whitespace collapsed, lowercased) containment or similarity >= 0.9; reasoning-start must NOT appear in the copy; copy not empty and >= 50% of DOM length; failure logs numbers and falls back ("Dom (copy rejected)").
- Parser: outer code fence stripped only when first AND last non-blank lines are fences of same/greater length; inner fences untouched.
- System prompt: no outer code fence, plain AgentScript; nonce reminder kept.
- doctor: MessageCopyButton match, capture strategy used (interception/clipboard/none), clipboard left unchanged, copied length, validation result; --capture saves trimmed action-bar HTML.
- Turn log records the reply source per reply.

## Task 3 — docs rewrite (README, docs/*, CLI README) + consistency tests; DOCS-TODO deleted.

## Task 1 additions (final)
- AgentTurnResult.ErrorCode (KA_ code on turn failure).
- ConfirmAsync shows the bracketed default (Spectre ShowChoices), Enter accepts it, no loop.
- AgentLoop dirty menu: carry(default)/stash/here(non-protected)/cancel; branch menu new(default)/stay/cancel only on clean non-protected; protected+clean = auto agent branch, zero menus.
- GitService: IsInstalledAsync, InitRepositoryAsync (-b main with fallback), EnsureDotnetGitignoreAsync (dotnet new gitignore or minimal template), CreateInitialCommitAsync (one-shot -c identity), HasOriginRemoteAsync.
- CommitExecutor: no origin -> Ok "committed locally, push skipped".

## Task 2 additions (final)
- ReplySourceMode (Auto/Copy/Dom, default Auto) + Browser.ReplySource config; selector key MessageCopyButton (3 fallbacks incl. aria-label Copy).
- IChatPage capture members: GetNewestReasoningTextAsync, WaitForMessageCopyButtonAsync (hover first), PrepareReplyCaptureAsync (ExposeBinding + init script wrapping writeText/execCommand/copy event, agent-flag gated), TryCaptureCopiedReplyAsync (interception -> OS clipboard read with grant + save/restore in finally).
- IReplyReader logic in Core (ReplyReader): Copy-first per mode, validation (normalized containment or similarity >= 0.9, reasoning-start must be absent, >= 50% DOM length), Auto falls back with "Dom (copy rejected)" origin, Copy fails KA_REPLY_TIMEOUT.
- IChatSession.LastReplyOrigin ("Copy" / "Dom" / "Dom (copy rejected)"); AgentLoop logs reply-source per reply into the turn log.
- Parser fence tolerance: outer fence stripped only when first AND last non-blank lines are fences of same/greater length (3 or 4 backticks); inner fences untouched.
- System prompt: no outer code fence; nonce reminder.
- doctor: MessageCopyButton probe + ReplyCapture check (strategy, clipboardUntouched, copiedLength, validation); --capture unchanged; capture probe uses a PlaywrightChatPage over the same tab.
- ExposeBindingAsync<string,string?> interception; OS clipboard always restored in finally.
