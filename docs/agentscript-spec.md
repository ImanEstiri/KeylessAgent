# AgentScript v1 — grammar, commands, and error codes

AgentScript is the only format the agent executes. A model reply is a sequence of **blocks** followed by a final `@@eof` line. Anything else is a format violation: the reply is rejected (nothing executes) and the model is asked to resend.

## Grammar

```ebnf
response   := fence? block* "@@eof" fence? ;
block      := "@@" command [ argument ] newline
              section*
              "@@end" newline ;
section    := "<<<" NAME [ ":" nonce ] newline
              content-line*
              NAME [ ":" nonce ] ">>>" newline ;
command    := "say" | "newfile" | "updatefile" | "editfile" | "deletefile"
            | "readfile" | "mkdir" | "runcommand" | "ask" | "commit" | "done" ;
NAME       := "MSG" | "CONTENT" | "FIND" | "REPLACE" | "CMD" ;
```

Rules:

- Command names are **case-insensitive**.
- **Paths are relative to the workspace root and use `/`** — never absolute, never `..`, never backslashes, never inside `.git` or `.agent`.
- Payload markers only count when they are **the whole line** (trimmed). File content may safely contain the same text mid-line.
- **Nonce markers**: if a file must contain a line equal to its end marker, the model can use `<<<CONTENT:x7` … `CONTENT:x7>>>`.
- A surrounding Markdown code fence is tolerated and stripped.
- Non-whitespace text outside blocks is a violation.
- The agent numbers commands by their order in the response (`#1`, `#2`, …); the model does not number them.
- Everything the model writes is **English**; the only exception is file content.

## Commands

| Command | Argument | Payloads | Semantics |
| --- | --- | --- | --- |
| `say` | — | `MSG` | Prints the message to the user. The only way the model talks. Does **not** count toward the batch limit. |
| `newfile` | path | `CONTENT` | Creates the file. Fails (`KA_FILE_ALREADY_EXISTS`) if it exists. Missing parent folders are created. The line ending comes from .editorconfig (end_of_line) for that path, else .gitattributes (eol=…), else the dominant ending of sibling files, else the platform default. |
| `updatefile` | path | `CONTENT` | Replaces the whole file. Fails if missing. BOM and the file's own line ending are preserved (the model's LF content is converted). |
| `editfile` | path | `FIND`, `REPLACE` | Replaces the single occurrence of FIND. Fails unless FIND matches exactly once. BOM is preserved. Matching and counting run on LF-normalized text, and the written file keeps its original (dominant) line ending — a CRLF file stays 100% CRLF. |
| `deletefile` | path | — | Backs up, then deletes (confirmed in Ask mode). |
| `readfile` | path | — | Returns the file content as that command's result. |
| `mkdir` | path | — | Creates the folder (and parents); idempotent, never confirmed |
| `runcommand` | — | `CMD` | Runs one command line. Every segment split on `&&`, `||`, `;`, `|` is checked against the policy. Optional argument `on_error=continue` or `on_error=stop` overrides the batch failure mode for that command. |
| `ask` | — | `MSG` | Asks the user; the answer becomes that command's result. |
| `commit` | — | `MSG` | `git add -A`, commit, `git push -u origin <branch>`. Push failures are reported to the model. |
| `movefile` | from path | `TO` | Moves/renames a file. Sandboxed, backed up, refuses to overwrite (`KA_FILE_ALREADY_EXISTS`), creates parents. |
| `next` | — | `MSG` (optional) | Control directive: run this batch, send results, keep waiting. Commands without a directive default to @@next. |

### Example

```text
@@say
<<<MSG
I will add the service now.
MSG>>>
@@end
@@newfile src/Greeter.cs
<<<CONTENT
namespace App;

public static class Greeter
{
    public static string Hello(string name) => $"Hello, {name}!";
}
CONTENT>>>
@@end
@@runcommand
<<<CMD
dotnet build
CMD>>>
@@end
@@done
<<<MSG
Added src/Greeter.cs; build passed.
MSG>>>
@@end
@@eof
```

## Command risk tiers

Every command segment is classified:

| Tier | Examples | Ask mode | AutoSafe | Auto |
| --- | --- | --- | --- | --- |
| ReadOnly | git status/log/diff, dotnet --version, node -v, dir, type, cat, echo | auto-approved | auto-approved | auto |
| Build | dotnet build/restore/clean | auto-approved | auto-approved | auto |
| Execute | dotnet test/run, npm test/run, node file, python file | confirm | auto if allow-listed | auto |
| Risky | python -c, node -e, redirection, installs, deletes, git writes, unknown programs | confirm | confirm (always, even AutoSafe) | auto |
| Denied | format, shutdown, force-push, git config --global, curl \| sh | never runs | never runs | never runs |

## Batch failure modes

`Commands.BatchFailureMode` controls what happens after a failed command:
- **StopOnMutationFailure** (default): a ReadOnly probe failure never stops the batch (probes are independent); a Build/Execute/Risky/file-command/@@commit failure stops the remaining non-say commands (reported as skipped with "batch stopped at command #N").
- **StopOnAnyFailure**: any failure stops the rest.
- **Continue**: nothing stops.

Use `@@runcommand on_error=continue` to override the mode for that command only. Failed command output is always returned to the model in full.

## Limits (enforced, injected into the system prompt)

| Limit | Default | Behavior when exceeded |
| --- | --- | --- |
| `MaxCommandsPerResponse` | 5 | First N non-`say` commands execute; the rest are reported as `skipped` and resent by the model. The EFFECTIVE limit is min(Agent.Pace limit, this value) — see Pacing below |
| `MaxResponseChars` | 60000 | Reply rejected (`KA_LIMIT_RESPONSE_CHARS`); model told to split |
| `MaxFileContentChars` | 40000 | Reply rejected (`KA_LIMIT_FILE_CONTENT_CHARS`) |
| `MaxCommandOutputChars` | 8000 | Result output capped (head + tail kept, cut marked) |
| `MaxFormatRetries` | 3 | After N invalid replies the turn stops and the user sees the raw last reply |
| `MaxIterationsPerTurn` | 25 | Turn stops with a stop reason |

## Pacing (Agent.Pace)

`Agent.Pace` (keyless.json, or `--pace` on `run`/interactive) caps how many NON-`say` commands one response may carry: **Step** = 1, **Balanced** = 3 (default), **Batch** = `Limits.MaxCommandsPerResponse`. The effective limit is `min(Pace limit, Limits.MaxCommandsPerResponse)` and is injected into the system prompt.

When a response carries more commands than the effective limit, the first N execute and the rest are reported as skipped with the reason:

```text
pace=<Pace>: only N command(s) per response; send the next one after reading this result
```

(The generic "batch limit … resend them next" note is used only in Batch mode.) In Step mode `MaxIterationsPerTurn` is raised to at least 60 for the turn, and the system prompt/reminder carry the Step rule: one command, then `@@next`, verify the result before continuing.

## Verified line

Successful `newfile`, `updatefile`, `editfile`, `movefile` and `mkdir` results carry one compact line the model can trust without re-reading the file:

```text
verified: path=<p> bytes=<n> lines=<n> sha=<8 hex> first="<first 60 chars>" last="<last 60 chars>"
```

`mkdir` results carry `verified: dir exists`. Newlines inside the snippets are shown as `
` escapes; the line is capped at 300 characters and never contains the full file content. CRLF files count both bytes per line ending; lines are counted after CRLF/LF normalization.

## @@done ends the turn

A successful `@@done` finishes the turn immediately: commands placed AFTER it in the same reply are reported as `skipped` with the reason `after @@done` and are never executed. `@@say` blocks after `@@done` still reach the user.

## Results message

After each executed batch the agent sends one message mapping every result to its command:

```text
[KEYLESSAGENT RESULTS] turn=3 batch=2
--- result 1/4 | #1 newfile | src/Foo/Bar.cs ---
status: ok
--- result 2/4 | #2 editfile | src/Foo/Baz.cs ---
status: error
reason: FIND text matched 0 times (must match exactly once)
--- result 3/4 | #3 runcommand | dotnet build ---
status: ok | exit code: 0 | duration: 4.2s
<<<OUTPUT
...
OUTPUT>>>
--- skipped 1 commands: batch limit is 4 per response, resend them next ---
[END RESULTS]
Continue with the next commands, or finish with @@done. Reply in AgentScript v1 only.
```

## Format corrections

Invalid replies get an immediate, specific correction (nothing executes):

```text
[KEYLESSAGENT FORMAT ERROR]
Your last response was rejected. Nothing was executed.
Problems:
- line 14: Unknown command "@@createfile".
- Missing "@@eof" at the end (the response looks truncated).
Your response looks truncated. Resend it in SMALLER pieces (fewer commands per response).
Resend your COMPLETE response using only AgentScript v1. Do not apologize or explain. Do not add text outside blocks.
```

## Error-code catalog

Every failure the agent reports carries a stable code.

### Parsing / format

| Code | Meaning |
| --- | --- |
| `KA_PARSE_UNKNOWN_COMMAND` | `@@` keyword is not a known command |
| `KA_PARSE_STRAY_TEXT` | non-whitespace text outside blocks (or inside a block, outside a payload) |
| `KA_PARSE_MISSING_EOF` | reply does not end with `@@eof` |
| `KA_PARSE_TEXT_AFTER_EOF` | non-whitespace text after `@@eof` |
| `KA_PARSE_UNTERMINATED_BLOCK` | block opened without `@@end` |
| `KA_PARSE_MISSING_PAYLOAD` | required section (`MSG`/`CONTENT`/`FIND`/`REPLACE`/`CMD`) missing |
| `KA_PARSE_UNEXPECTED_PAYLOAD` | section given to a command that does not take one |
| `KA_PARSE_MISSING_ARGUMENT` | path missing on a command that needs one |
| `KA_PARSE_BAD_MARKER` | malformed `<<<` marker or unknown section name |
| `KA_PARSE_BAD_PATH` | path absolute, contains `..`/`.`, or uses backslashes |
| `KA_REPLY_EMPTY` | reply is empty |

### Limits

| Code | Meaning |
| --- | --- |
| `KA_LIMIT_RESPONSE_CHARS` | reply exceeds `MaxResponseChars` |
| `KA_LIMIT_FILE_CONTENT_CHARS` | one payload exceeds `MaxFileContentChars` |
| `KA_TIMEOUT` | process exceeded its timeout |

### Workspace execution

| Code | Meaning |
| --- | --- |
| `KA_PATH_OUTSIDE` | resolved path escapes the workspace, or is inside `.git`/`.agent` |
| `KA_FILE_NOT_FOUND` | target file does not exist |
| `KA_FILE_ALREADY_EXISTS` | `newfile` target exists |
| `KA_FIND_NOT_UNIQUE` | `editfile` FIND matched ≠ 1 times |
| `KA_FILE_IO` | file/backup I/O failure |

### Policy / git

| Code | Meaning |
| --- | --- |
| `KA_COMMAND_DENIED` | blocked by the command policy |
| `KA_COMMAND_DECLINED` | the user declined a confirmation |
| `KA_COMMAND_FAILED` | command ran but exited non-zero |
| `KA_COMMAND_NOT_FOUND` | the program of a command does not exist on this machine |
| `KA_CANCELLED` | the command was cancelled by the user (Ctrl+C); partial output is kept |
| `KA_PARSE_BAD_OPTION` | unknown command option or option value |
| `KA_PARSE_CONFLICTING_CONTROL` | both @@next and @@done in one reply |
| `KA_PARSE_SAY_ONLY` | reply contains only @@say and no control directive |
| `KA_BRANCH_PROTECTED` | current branch matches a protected pattern |
| `KA_NOT_A_GIT_REPOSITORY` | workspace is not a git repo |
| `KA_GIT_FAILURE` | a git command failed |
| `KA_GIT_NOT_INSTALLED` | git is not installed (workspace not a repo, `Git.AutoInit` needs it) |
| `KA_GIT_INIT_FAILED` | automatic repository initialization failed |

### Chat / browser

| Code | Meaning |
| --- | --- |
| `KA_BROWSER_CONNECTION_FAILED` | cannot attach to / launch Chrome |
| `KA_SELECTOR_NOT_FOUND` | a required selector did not match |
| `KA_REPLY_TIMEOUT` | no complete reply within the timeout |
| `KA_REPLY_TRUNCATED` | reply stream stayed truncated (AgentScript without @@eof) through 20 rejected attempts in one wait |
| `KA_SEND_NOT_CONFIRMED` | the message could not be sent (not confirmed in the DOM, retry failed too) |
| `KA_ATTACHMENT_UPLOAD_FAILED` | file upload failed (falls back to pasting) |
| `KA_NETWORK_OFFLINE` | the machine is offline — nothing was uploaded or sent; the chat session stays valid, resend the same request |
| `KA_TOOL_MISSING` | a runcommand tool is listed as Missing in the environment — the process is never started; do not retry it |
| `KA_COMPOSER_NOT_CLEAN` | the composer still holds stale text or attachment chips after the reset attempt (run `keyless doctor` to fix the AttachmentRemove/AttachmentFailed selectors) |
| `KA_CHAT_EXHAUSTED` | chat hit its length limit |

### Configuration / snapshot / internal

| Code | Meaning |
| --- | --- |
| `KA_CONFIG_INVALID` | `keyless.json`/`selectors.json` is not valid JSON |
| `KA_CONFIG_VALIDATION` | a configuration value is out of range |
| `KA_SNAPSHOT_MISSING` | a bat file or its output is missing |
| `KA_SNAPSHOT_RUN_FAILED` | a snapshot bat run failed |
| `KA_SESSION_NOT_FOUND` | no saved session (expected on first run) |
| `KA_INTERNAL` | unexpected internal failure (bug guard) |
