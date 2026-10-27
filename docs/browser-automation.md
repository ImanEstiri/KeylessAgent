# Browser automation

The highest-risk part of KeylessAgent: driving the DeepSeek web chat with Microsoft.Playwright. This document explains what is automated, which selectors are used, and what still needs manual verification.

## Connection — the Chrome 136 rule and the one-time login

Since **Chrome 136**, `--remote-debugging-port` is **ignored when Chrome uses its default
user-data-dir**. Attaching to your daily Chrome profile is therefore impossible. KeylessAgent
uses a **dedicated profile** (`Browser.UserDataDir`, default
`%LOCALAPPDATA%\KeylessAgent\chrome-profile`) and you log in to DeepSeek **once**; the login
persists there.

Connection order (every step reported through the UI — never silent):

| Step | Behavior |
| --- | --- |
| 1. Probe | `GET http://127.0.0.1:<port>/json/version` — open → attach ("Attached to Chrome on port N"). |
| 2. Auto-launch (default on) | Start the REAL Chrome: `chrome --remote-debugging-port=<port> --remote-debugging-address=127.0.0.1 --user-data-dir=<profile> --no-first-run --no-default-browser-check https://chat.deepseek.com/`; wait ≤30s for the port; attach; "Started Chrome with the KeylessAgent profile. Log in to DeepSeek in that window once." |
| 3. Fallback | Only with `Browser.AllowPlaywrightLaunch: true` (default **false**): a Playwright-managed browser with its own profile, explicitly warned as separate/automated. |
| Failure | Chrome not found/started → `KA_BROWSER_CONNECTION_FAILED` with the exact manual-start command line; port open but attach failed → the reason is reported instead of falling through. |

Tab rules: an existing `chat.deepseek.com` tab is reused (`Browser.ReuseExistingTab`, default
true) — **your other tabs are never navigated**; otherwise a new TAB opens in the existing
window (`Target.createTarget … newWindow:false`). The chosen tab is focused
(`BringToFrontAsync` + `Target.activateTarget`). When a login is required the tab is brought
to front again and the agent waits (up to `LoginWaitSeconds` = 5 minutes), continuing
automatically once the login indicator disappears.

Dispose policy: only the tab the agent opened is closed; the Chrome window stays running
(`Browser.CloseChromeOnExit: false`) so the next run reuses the warm session. `run`, `doctor`
and `keyless chrome` all go through the same connection service (`IBrowserConnector`), so
doctor reports which path was taken (attached / auto-launched / failed) and why. `keyless
chrome <workspace>` starts only the dedicated Chrome for a first login.

## Selectors

All selectors live in the workspace `selectors.json` (created from the embedded default on first run), each as an ordered fallback list — **editable without recompiling**. The default catalog was built from a real DOM sample of the chat page and prefers structural selectors over the obfuscated hashed class names (which change between releases):

| Key | Default (first fallback) | Notes |
| --- | --- | --- |
| `InputBox` | `textarea[placeholder="Message DeepSeek"]` | also `textarea[name="search"]` |
| `SendButton` | `div:has(> input[type="file"]) ~ div [role="button"].ds-button--filled` | the filled/primary button next to the file input — NEVER the paperclip (`iconLabelPrimary` opens the OS file dialog); disabled/paperclip-like candidates are skipped, retry is Enter-only |
| `FileInput` | `input[type="file"][multiple]` | accepts `.md`, `.txt`, `.bat`, `.cs`, `.json`, … |
| `NewChatButton` | `[role="button"]:has-text("New chat")` | not present in the sample; multiple fallbacks |
| `MessageItems` | `.ds-virtual-list [data-virtual-list-item-key]` | the list is virtualized: only newest messages exist in the DOM |
| `AssistantMessage` | `.ds-message:has(.ds-markdown.ds-assistant-message-main-content)` | |
| `FinalAnswer` | `.ds-markdown.ds-assistant-message-main-content` | never matches inside `.ds-think-content` |
| `Reasoning` | `.ds-think-content` | excluded from every read |
| `StopIndicator` | `[role="button"][aria-label="Stop"]` | not in the sample; several fallbacks |
| `DeepThinkToggle` / `SearchToggle` | `.ds-toggle-button:has-text("DeepThink")` / `…("Search")` | state read from `aria-pressed` |
| `LoginIndicator` | `input[type="password"]` | |

**Still unverified against a live chat**: the generating/Stop state, the New Chat button, and the attachment chips. Those selectors ship with several fallbacks and log a clear error naming the missing selector — fix them in `selectors.json` when DeepSeek changes, or just run `keyless doctor` which reports every key with the matching fallback index.

## Cancellation

Every page operation takes a CancellationToken. Playwright's own waits do not accept one, so each call races the token — Ctrl+C interrupts any in-flight wait promptly instead of waiting out its timeout.

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

## Send / confirm / upload decision tables

**Send attempt** (one attempt = the send button when clickable, otherwise Enter — never both):

| Observation after the attempt | Decision |
| --- | --- |
| A new message item appeared in the DOM | Sent — continue to `WaitForReplyAsync` |
| Input is empty AND the stop/generating indicator is on | Sent |
| Input is empty, no indicator (virtualized list did not grow) | Sent — a constant item count proves nothing |
| Input still holds the text | Not sent — retry exactly once, with ENTER (a repeated click on a mis-identified control only opens dialogs) |
| Still not confirmed after the retry AND the input still holds the text | Fail `KA_SEND_NOT_CONFIRMED` |

**Attachment delivery** (after `SetInputFilesAsync` is attempted):

| Observation | Decision |
| --- | --- |
| `SetInputFilesAsync` itself failed | Paste the content into the message |
| The hidden file input reports one of the uploaded file names | Attached — send with the files |
| A composer-near element (chip) shows an uploaded file name | Attached |
| Chips are visible at all | Attached (uncertainty counts as attached, with a warning) |
| No sign of the files after `Browser.UploadWaitSeconds` | Paste the content instead |

The same content is never both attached and pasted.

## Reply sources — Copy button first, DOM fallback (`Browser.ReplySource`)

Verified facts from real chats: the message-level Copy button copies ONLY the final answer
(never the reasoning), the copied text is raw Markdown (indentation, markers, nested fences
preserved), and it may or may not carry an outer code fence.

| Mode (`Browser.ReplySource`) | Behavior |
| --- | --- |
| `Auto` *(default)* | Try Copy → validate → fall back to Dom. The used source is logged per reply. |
| `Copy` | Copy only; capture or validation failures return `KA_REPLY_TIMEOUT` (never silent fallback). |
| `Dom` | The original DOM extraction. |

**Capture strategies, in order:**

1. **Interception**: an injected script wraps `navigator.clipboard.writeText`, `document.execCommand('copy')` and the `copy` event, reporting the text to C# through an exposed binding — active ONLY while the agent's capture flag is set, and the OS clipboard is never written, so the user's manual copying is unaffected.
2. **Clipboard read**: grant `clipboard-read`/`clipboard-write`, save the current clipboard text, hover the newest message and click its message-level Copy control, read `navigator.clipboard.readText()`, and ALWAYS restore the previous text in a `finally` block.
3. **None**: fall back to Dom (Auto) or fail with `KA_REPLY_TIMEOUT` (Copy).

**Validation** (rendered DOM text and raw Markdown differ by syntax, so normalized forms are compared — markdown/punctuation stripped, whitespace collapsed, lowercased): (a) the normalized DOM text is contained in the normalized copy, OR similarity ≥ 0.9 (token overlap + length ratio); (b) the first 60 normalized characters of the reasoning block must NOT appear in the copy; (c) the copy is non-empty and at least 50% of the DOM length. On failure the numbers are logged and Auto falls back ("Dom (copy rejected)").

Completion detection is unchanged (new-message fingerprint, final-answer element, stop indicator gone, stable); with Copy the message-level Copy control of THAT newest message must also appear (hover first — the bar may be hover-only), otherwise Dom fallback (Auto) or `KA_REPLY_TIMEOUT` (Copy). The parser strips an outer code fence only when the first AND last non-blank lines are fences of the same or greater length, so a copied reply wrapped in a fence parses fine while inner fences survive.

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
