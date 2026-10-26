# Browser automation

The highest-risk part of KeylessAgent: driving the DeepSeek web chat with Microsoft.Playwright. This document explains what is automated, which selectors are used, and what still needs manual verification.

## Connection

1. **Default — attach to your Chrome**: you start Chrome with `chrome --remote-debugging-port=9222`; the agent uses `ConnectOverCDP` and reuses your logged-in profile. Nothing is stored, no passwords are ever typed.
2. **Fallback — persistent profile**: if CDP fails, the agent launches Chromium with a persistent context under `%LOCALAPPDATA%\KeylessAgent\chrome-profile`. Log in once; the login persists.

If the login page is detected, the agent prints a warning and waits (up to 5 minutes) for you to log in manually.

## Selectors

All selectors live in the workspace `selectors.json` (created from the embedded default on first run), each as an ordered fallback list — **editable without recompiling**. The default catalog was built from a real DOM sample of the chat page and prefers structural selectors over the obfuscated hashed class names (which change between releases):

| Key | Default (first fallback) | Notes |
| --- | --- | --- |
| `InputBox` | `textarea[placeholder="Message DeepSeek"]` | also `textarea[name="search"]` |
| `SendButton` | `div:has(> input[type="file"]) [role="button"].ds-button--filled` | icon-only button next to the hidden file input |
| `FileInput` | `input[type="file"][multiple]` | accepts `.md`, `.txt`, `.bat`, `.cs`, `.json`, … |
| `NewChatButton` | `[role="button"]:has-text("New chat")` | not present in the sample; multiple fallbacks |
| `MessageItems` | `.ds-virtual-list [data-virtual-list-item-key]` | the list is virtualized: only newest messages exist in the DOM |
| `AssistantMessage` | `.ds-message:has(.ds-markdown.ds-assistant-message-main-content)` | |
| `FinalAnswer` | `.ds-markdown.ds-assistant-message-main-content` | never matches inside `.ds-think-content` |
| `Reasoning` | `.ds-think-content` | excluded from every read |
| `StopIndicator` | `[role="button"][aria-label="Stop"]` | not in the sample; several fallbacks |
| `DeepThinkToggle` / `SearchToggle` | `.ds-toggle-button:has-text("DeepThink")` / `…("Search")` | state read from `aria-pressed` |
| `LoginIndicator` | `input[type="password"]` | |

**Not covered by the DOM sample**: the generating/Stop state, the New Chat button, and the login page. Those selectors ship with several fallbacks and log a clear error naming the missing selector — fix them in `selectors.json` when DeepSeek changes.

## How to verify selectors: keyless doctor

`keyless doctor [<workspace>]` attaches to Chrome exactly like `run`, opens chat.deepseek.com
in a **new tab**, and reports every selectors.json key without sending a message or changing
the chat:

- **MATCHED** (with which fallback index and the element count) — the selector works.
- **NOT FOUND** — no fallback matched although the element is required; fix selectors.json.
- **NOT APPLICABLE NOW** — the element legitimately cannot exist in the current state
  (Stop while nothing generates, chips with nothing attached, Reasoning without a thinking
  message, FinalAnswer on a fresh chat, LoginIndicator while logged in).

It also prints the logged-in state, the DeepThink/Search `aria-pressed` values, whether the
input accepts text, and the page URL. **Exit code 0 only when every required selector for the
current state matched** (InputBox, SendButton, FileInput, NewChatButton, MessageItems,
AssistantMessage, DeepThinkToggle, SearchToggle).

Options:

- `--capture <dir>` writes trimmed HTML snapshots (scripts, styles, svg removed; message text
  replaced with "...") of the composer, the newest message, the sidebar and any attachment
  chips — attach them to bug reports instead of screenshots of real content.
- `--watch` waits while you type a prompt and send it manually, then captures the generating
  state (stop indicator) and the finished state once each — the two states the unit tests
  cannot see.

Run `keyless doctor` after every DeepSeek UI update: a NOT FOUND line points at exactly which
fallback list to fix.

## Reply identity — never the previous reply

After every send the DOM still shows the PREVIOUS assistant message until the new one renders
(the list is virtualized; the stop indicator may also lag). To never return the old reply:

1. **Before sending**, the session fingerprints the newest assistant message: its
   `data-virtual-list-item-key` when present, otherwise a stable hash of its final-answer text,
   plus the count of visible assistant messages (count alone is never trusted — virtualization).
2. **The send itself is confirmed** within `Browser.SendConfirmSeconds` (default 15): a new
   message item appeared in the DOM, or the input is empty and the stop/generating indicator
   is on. Otherwise the send is retried once (click, then Enter) and then fails with
   `KA_SEND_NOT_CONFIRMED`.
3. **A reply is accepted only when all of these hold**: its fingerprint differs from the
   pre-send one (identical text is valid only with a different message key), the final-answer
   element exists, the text is stable for two reads one second apart, and the stop indicator
   is gone.

## Uploads — wait before sending

After uploading attachments the send click waits until the upload settles (`Browser.UploadWaitSeconds`,
default 60): the file chips are visible and the send button is enabled. On timeout (or when the
file input cannot be used at all) the content is pasted into the message instead.

## Reading replies — final answer only

Each assistant message can contain **two** `.ds-markdown` blocks: the reasoning (`.ds-think-content`, headed "Thought for N seconds") and the final answer (`.ds-markdown.ds-assistant-message-main-content`). Only the **final answer of the newest assistant message** is ever read; the reasoning never reaches the parser (it would break it), the logs, or the chat.

`IReplyExtractor` (unit-tested against fragments of the real sample) takes the newest message's HTML and returns only the final-answer text:

- the reasoning subtree is skipped entirely,
- inside code blocks only the `<pre>` text is kept (the Copy/Download banner is dropped),
- `&lt;`/`&amp;`/… entities are decoded,
- text inside `<pre>` is taken raw — indentation and newlines are preserved rather than reading the rendered `innerText` of the Markdown.

**Completion detection**: the session waits while the stop/generating indicator is visible, then reads the final-answer element twice one second apart and accepts it when unchanged. A message with reasoning but no final-answer element yet never counts as a reply.

## Sending

- Long text is inserted with a whole-text insert (`keyboard.insertText`), **never per-character typing**.
- A local rate limiter enforces `MaxMessagesPerMinute` plus random delays between `MinDelayMs`/`MaxDelayMs`.
- Attachments are first **uploaded** through the chat's file input (`SetInputFilesAsync` on files written to `.agent/attachments`). When upload is impossible or the content is too big, it is **pasted** into the message; oversized dumps are split at file boundaries into consecutive parts.

## Toggles

Before the first message of every new chat, both toggles (DeepThink, Search) are checked via `aria-pressed` and clicked only when they disagree with `Browser.EnableDeepThink` / `Browser.EnableSearch`, then verified. A missing toggle is a warning, not an error.

## Recovery

- Empty replies / "server busy": retried with backoff inside the reply timeout.
- Captcha: not solved — the user solves it in the browser while the agent waits.
- Debug mode (`-v` / `Browser.Debug`): screenshots on errors into `.agent/logs/`, structured diagnostics log. Cookies, tokens and page HTML are never logged.

## ⚠️ Manual verification still required

Built from a DOM sample, verified only by unit tests so far. Before relying on it, verify against a **real chat**:

1. **Code indentation**: send a reply containing an indented code block and confirm the parsed AgentScript preserves leading spaces (the extractor reads raw `<pre>` text nodes for this reason — but confirm on the live DOM).
2. **Stop indicator**: confirm the selector matches the generating state (falls back to stability-based detection otherwise).
3. **New Chat button** and **login page** selectors.
4. **File upload**: confirm `.md`/`.txt` uploads render for the model; otherwise switch `AttachOnFeedback`-style pasting happens automatically.
5. Toggle labels (`DeepThink`/`Search` texts) and `aria-pressed` values.

Record the results of this checklist in this file after testing.
