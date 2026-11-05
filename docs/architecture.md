# Architecture

KeylessAgent mirrors the layout of [DepLens](https://github.com/ImanEstiri/DepLens): one solution, an `Abstractions` project with models and contracts, a `Core` project with all implementations wired by a hand-written factory, and UI projects that contain no business logic.

## Layers

```text
┌────────────────────────────────────────────┐
│ UI: ImanSoftware.KeylessAgent.Cli          │  Spectre.Console.Cli; implements IAgentUserInterface
│     (later: Windows Forms project)         │  references ONLY Core
├────────────────────────────────────────────┤
│ Core: ImanSoftware.KeylessAgent.Core       │  AgentLoop + services (internal sealed classes)
│        KeylessAgentServiceFactory          │  hand wiring, no DI container
├────────────────────────────────────────────┤
│ Abstractions                              │  models, enums, service contracts,
│                                           │  IAgentUserInterface (the UI contract)
└────────────────────────────────────────────┘
```

**Dependency rule:** dependencies point downward only. Core never references Spectre.Console, `System.Console`, or any UI library — everything the user sees goes through `IAgentUserInterface` (`Say`, `ShowProgress`, `ConfirmAsync`, `AskAsync`, `ShowDiff`). Playground and Tests reach Core internals through `InternalsVisibleTo`.

## Services

| Contract (Abstractions) | Implementation (Core) | Responsibility |
| --- | --- | --- |
| `IAgentScriptParser` | `Parsing/AgentScriptParser` | line-based state machine over AgentScript v1; line-numbered problems |
| `IAgentScriptValidator` | `Parsing/AgentScriptValidator` | size limits + path shape |
| `IResponseFormatGuard` | `Parsing/ResponseFormatGuard` | the single gate in front of execution |
| `IReplyExtractor` | `Browser/ReplyExtractor` | final-answer text from a message's HTML (never the reasoning) |
| `IPathSandbox` | `Execution/PathSandbox` | workspace-relative only; blocks `..`, absolute, `.git`/`.agent`, symlink escapes |
| `ICommandPolicy` | `Execution/CommandPolicy` | deny list + allow list per segment |
| `IWorkspaceCommandExecutor` | `Execution/WorkspaceCommandExecutor` | dispatches to per-command executors (say/newfile/updatefile/editfile/deletefile/readfile/runcommand/ask/commit/done) |
| `IBackupService` | `Execution/BackupService` | `.agent/backups` batches; `keyless undo` |
| `IGitService` | `Git/GitService` | short-lived `git` processes; never force/reset/branch-delete/merge |
| `IBranchGuard` | `Git/BranchGuard` | protected-branch patterns enforced in code |
| — | `Git/BranchNameBuilder` | `agent/<yyyyMMdd-HHmm>-<slug>` |
| `ISnapshotService` | `Snapshot/SnapshotService` + `SnapshotFilter` + `WorkspaceAnalyzer` | generates the tree/code-dump snapshots natively in C# (no workspace scripts needed); filters/chunks attachments |
| `IResultFormatter` | `Agent/ResultFormatter` | `[KEYLESSAGENT RESULTS]` messages |
| `IChatProvider` / `IChatSession` | `Browser/PlaywrightChatProvider` / `PlaywrightChatSession`, `Browser/FakeChatProvider` | the browser behind an interface; fake for tests/Playground |
| `IEnvironmentTools` / `EnvironmentProbe` | probes git/dotnet/node/npm/python (24 h cache, per-tool 1 h freshness, invalidation); powers the fail-fast tool rule and the UIs' Tools line |
| `ISessionStore` | `Agent/SessionStore` | `.agent/session.json` (chat URL, branch, turn) |
| `IConfigurationService` | `Configuration/ConfigurationService` | tolerant `keyless.json` load/validate/create |
| `IAgentLoop` | `Agent/AgentLoop` | one call = one turn; orchestrates everything |

## Result convention

Every service method that can fail returns `Outcome` / `Outcome<T>` (`ImanSoftware.Outcome`), with stable `KA_*` error codes (catalog: [agentscript-spec.md](agentscript-spec.md)). `Data` is only read after `IsSuccess` is checked — and because the package's `Data` throws on null, contracts never carry null data (e.g. "no session" is a `KA_SESSION_NOT_FOUND` failure).

## Adding a new UI

1. Reference `ImanSoftware.KeylessAgent.Core` only.
2. Implement `IAgentUserInterface`.
3. Create the loop via `KeylessAgentServiceFactory.CreateAgentLoop(loggerFactory)` and call `RunAsync(workspaceRoot, request, ui, dryRun, ct)` per request. Multi-turn interactive UIs simply call it repeatedly — the chat session is persisted and reused.
