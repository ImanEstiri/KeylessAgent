# KeylessAgent WPF UI — Build Guide (for the AI agent)

You are going to add a **beautiful desktop UI (WPF)** to the KeylessAgent solution. This guide tells you exactly what to build, in which order, and how to check your own work. **Read sections 0 to 5 completely before you write anything. Then do ONLY the phase the user asks for.**

---

## 0. How to work (read this first, these rules are mandatory)

1. The user will say "Do Phase N". Do **only** that phase. When it is finished, stop with `@@done` and report. Never start the next phase by yourself.
2. You can send at most **5 commands per reply**. Create **one file per command**, keep every file **under ~200 lines** (split big XAML into several resource dictionaries or user controls). Use `@@next` between replies while you work, and `@@done` only when the phase is finished.
3. After every phase run `dotnet build` for the WPF project and `dotnet test` for its test project. Fix errors **one at a time** (read the first error, fix it, build again). Do not rewrite files you do not have to.
4. **Never run the WPF app.** Do not run `dotnet run` for the UI project, do not start `keyless`, do not start Chrome. The app opens a window and blocks forever. You cannot see the screen. Never say "the UI looks good": you can only say "it builds and the tests pass". The user checks the look.
5. **Do not edit** the Core projects (`ImanSoftware.KeylessAgent.Abstractions`, `ImanSoftware.KeylessAgent.Core`), the CLI project, or the Core tests, unless a phase explicitly says so. If you think Core needs a change, stop and report what and why.
6. Do not add NuGet packages. WPF needs none. (If you are really blocked, ask the user first.) If a package is ever allowed, add it with `dotnet add <project> package <Name>`, never write a version number in a `.csproj` (the solution uses Central Package Management).
7. Do not use `async void` (except WPF event handlers), do not call `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` on the UI thread.
8. Keep code-behind (`*.xaml.cs`) tiny: only view-only things (window dragging, focus, backdrop). All logic goes into ViewModels and services so it can be unit tested.
9. Every public type gets an XML doc comment (`/// <summary>`). Follow the style of the existing code (file-scoped namespaces, `internal sealed` where possible, `Outcome<T>` results for expected failures).
10. If analyzer warnings come from generated XAML code, add a **targeted** `<NoWarn>` in the WPF `.csproj` with a comment. Never lower rules in `Directory.Build.props`.
11. Add one bullet per change to `DOCS-TODO.md` in the repo root (create it if missing). Do not edit `README.md` or `docs/` before Phase 12.
12. Existing code is the best documentation. Before writing something, **read the matching CLI file** (listed in section 4). Copy the *idea*, not the Spectre.Console code.

---

## 1. Goal and non-goals

**Goal:** a Windows desktop app, `ImanSoftware.KeylessAgent.Wpf`, that does everything the CLI does, with a modern **dark, elegant, partly glass-like** look ("midnight and champagne gold"):

- choose a workspace (a git folder), type a request, press **Send**;
- watch the agent work as a live, animated activity feed (steps, messages from the model, commands, diffs);
- approve or decline commands and file changes in nice in-window dialogs (no console prompts);
- see the result of the turn (changed files, branch, timing), undo, run the doctor, view the settings.

**Non-goals (do not build):** a code editor, a terminal emulator, a Git GUI, themes other than dark + (optional) light, localization, auto-update, installer.

---

## 2. Facts about the environment

- Windows 10/11, .NET 10 SDK. WPF is included in the SDK on Windows. The new project must use `<TargetFramework>net10.0-windows</TargetFramework>`, `<UseWPF>true</UseWPF>`, `<OutputType>WinExe</OutputType>`. (The solution's `Directory.Build.props` probably sets `net10.0`; the new project must override it in its own `.csproj`.)
- The solution file is `KeylessAgent.slnx`. Add the new projects with `dotnet sln KeylessAgent.slnx add <path-to-csproj>`. If that command fails, open the `.slnx`, copy the style of the existing `<Project Path="..."/>` entries and add yours by hand.
- Central Package Management is on: package versions live only in `Directory.Packages.props`.
- Tests use **xunit v2**, **FluentAssertions 7.x**, `coverlet.collector`, `Microsoft.NET.Test.Sdk`. Copy the style of `src/tests/ImanSoftware.KeylessAgent.Tests/ImanSoftware.KeylessAgent.Tests.csproj` (its package references have no versions).
- Unverified assumptions you must **check** instead of trusting: the exact content of `Directory.Build.props` (analyzers, `TreatWarningsAsErrors`, language version), whether `dotnet sln` supports `.slnx` in the installed SDK.

---

## 3. Architecture

### 3.1 Folders

```text
src/UI/
├── ImanSoftware.KeylessAgent.Cli/            (exists, do not touch)
└── ImanSoftware.KeylessAgent.Wpf/            (NEW)
    ├── ImanSoftware.KeylessAgent.Wpf.csproj
    ├── App.xaml / App.xaml.cs
    ├── Mvvm/            ObservableObject, RelayCommand, AsyncRelayCommand
    ├── Infrastructure/  IUiDispatcher, WpfDispatcher, WindowBackdrop, BackdropPlanner,
    │                    SettingsService, CrashLogger, TextDirection
    ├── Services/        IDialogService + DialogService, WpfUserInterface, AgentRunner,
    │                    EventPresenter, LineDiff
    ├── ViewModels/      Shell, Agent, Workspace, Doctor, Settings, About, DialogHost, Feed items
    ├── Views/           ShellWindow, AgentView, WorkspaceView, DoctorView, SettingsView, AboutView,
    │                    dialogs and feed item controls
    ├── Themes/          Colors.xaml, Brushes.xaml, Typography.xaml, Glass.xaml, Controls.xaml,
    │                    Animations.xaml
    └── Converters/      small value converters
src/tests/
└── ImanSoftware.KeylessAgent.Wpf.Tests/      (NEW, net10.0-windows)
```

### 3.2 Dependency direction

`Wpf` → `Core` (project reference only). The UI knows `IAgentLoop`, `IAgentUserInterface` and the models; **Core never knows the UI**. ViewModels must not use WPF types such as `Brush`, `Window` or `MessageBox` (use enums, strings and plain numbers; the XAML maps them to brushes). That keeps them testable.

### 3.3 Threading model (very important)

Core runs on thread-pool threads. It can call **any** member of `IAgentUserInterface` from **any thread**, even at the same time.

- `Say`, `ShowProgress`, `ShowDiff`: fire and forget. Post them to the UI thread (`IUiDispatcher.Post`). Never block the caller.
- `ConfirmAsync`, `AskAsync`, `ChooseAsync`: return a `Task` that completes when the user answers in a dialog. Show **one dialog at a time** (queue the others with a `SemaphoreSlim`). The dialog lives on the UI thread; the `Task` is completed through a `TaskCompletionSource`.
- `BeginWait(message)`: show a busy indicator with the message and return an `IDisposable`. Disposing it hides that message. Calls may nest.
- When the user presses **Stop**: cancel the `CancellationTokenSource` of the turn **and** close any open dialog (complete it as declined/cancelled) so nothing hangs.
- Use an `IUiDispatcher` interface (`Post(Action)`, `InvokeAsync<T>(Func<T>)`) so tests can use an `ImmediateDispatcher`.

---

## 4. The Core API you may use (exact names)

Read these files before using them (they are in the repo): `src/UI/ImanSoftware.KeylessAgent.Cli/Helpers/AgentCliRunner.cs`, `ConsoleUserInterface.cs`, `InteractiveSession.cs`, `Commands/Chrome/ChromeCommand.cs`, `Commands/Doctor/DoctorCommand.cs`, `Commands/Undo/UndoCommand.cs`, `Commands/Config/ConfigCommand.cs`.

```csharp
// Abstractions: the contract your UI must implement
public interface IAgentUserInterface
{
    void Say(string message);                          // a message from the model to the user
    void ShowProgress(AgentProgressEvent progressEvent);
    Task<bool> ConfirmAsync(ConfirmRequest request);   // yes / no
    Task<string> AskAsync(string question);            // free text answer
    void ShowDiff(FileChange change);                  // a file is about to change
    Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default);
    IDisposable BeginWait(string message);
}

public sealed record AgentProgressEvent(AgentEventType Type, string Detail);
public enum AgentEventType {
    SessionStarting, ConnectingToBrowser, OpeningChat, UploadingAttachments, SendingMessage,
    WaitingForReply, ValidatingReply, RequestingResend, ExecutingCommand, AskingUser,
    SendingResults, RefreshingSnapshot, TurnFinished }

public sealed record ConfirmRequest(string Title, string Details);
public sealed record ChoiceOption(string Id, string Label, string? Description = null);
public sealed record ChoiceRequest(string Title, IReadOnlyList<ChoiceOption> Options, string DefaultId);
public sealed record ChoiceResult(string? SelectedId, bool Cancelled);
public enum FileChangeKind { Create, Update, Edit, Delete }
public sealed record FileChange(string Path, FileChangeKind Kind, string? OldContent, string? NewContent);

public interface IAgentLoop {
    Task<AgentTurnResult> RunAsync(string workspaceRoot, string request, IAgentUserInterface ui,
                                   bool dryRun = false, CancellationToken cancellationToken = default);
}
public sealed record AgentTurnResult {   // all init-only
    string Branch; int Turn; IReadOnlyList<string> Commits; string? Summary; bool CompletedByDone;
    string? StoppedReason; string? ErrorMessage; string? ErrorCode; bool DryRun;
    IReadOnlyList<ChangedPath> ChangeSet;   // ChangedPath has Kind and Path
    string? Timings; int AutoApprovedCommands; int ConfirmedCommands; string DiffStat; }
```

Creating services (static factory, no DI container):

```csharp
using ImanSoftware.KeylessAgent.Core.Factory;
var loop      = KeylessAgentServiceFactory.CreateAgentLoop(loggerFactory /* may be null */);
var config    = KeylessAgentServiceFactory.CreateConfigurationService();   // LoadAsync(workspaceRoot) -> Outcome<KeylessOptions>
var backups   = KeylessAgentServiceFactory.CreateBackupService();          // UndoLatestAsync(workspaceRoot) -> Outcome<IReadOnlyList<string>>
var sessions  = KeylessAgentServiceFactory.CreateSessionStore();
var inspector = KeylessAgentServiceFactory.CreateWorkspaceInspector();     // read its interface for the exact method
var doctor    = KeylessAgentServiceFactory.CreateChatDiagnostics();        // read DoctorCommand.cs for the exact call
var connector = KeylessAgentServiceFactory.CreateBrowserConnector();       // read ChromeCommand.cs for the exact call
var logs      = KeylessAgentServiceFactory.CreateFileLoggerFactory(workspaceRoot); // verbose diagnostics
```

`Outcome<T>` (namespace `ImanSoftware.Outcomes`): `IsSuccess`, `IsFailure`, `Data` (**throws** if failed, check first), `Error.Message`, `Error.Code`.
A turn that is cancelled throws `OperationCanceledException`. A turn that fails returns a result with `ErrorMessage` set. A turn stopped early has `StoppedReason`.

---

## 5. Design language (follow it exactly)

Name: **Midnight & Champagne**. Dark, calm, elegant. Gold is used sparingly (primary button, focus, active nav item, small highlights).

### 5.1 Colors (put them in `Themes/Colors.xaml`, brushes in `Brushes.xaml`; use `DynamicResource` in views)

| Token | Value | Use |
|---|---|---|
| `BgDeep` | `#080B16` | window base |
| `BgMid` | `#0E1428` | fallback gradient end |
| `GlassFill` | `#0FFFFFFF` (6 % white) | normal glass panel |
| `GlassFillHover` | `#1AFFFFFF` (10 %) | hover |
| `GlassFillStrong` | `#26FFFFFF` (15 %) | dialogs, composer |
| `GlassBorder` | `#24FFFFFF` (14 %) | 1 px panel border |
| `TextPrimary` | `#F2F4FA` | main text |
| `TextSecondary` | `#A9B1CC` | secondary text |
| `TextMuted` | `#6F7898` | hints |
| `Gold` / `GoldHover` / `GoldPressed` | `#E3C07A` / `#F0D29A` / `#C9A35F` | accent |
| `Violet` | `#8B7CFF` | secondary accent |
| `Success` / `Warning` / `Danger` / `Info` | `#5CD6A0` / `#F5B94C` / `#F26D7D` / `#6CB4FF` | status |
| `DiffAdd` / `DiffRemove` | `#265CD6A0` / `#26F26D7D` | diff row backgrounds |

### 5.2 Typography, spacing, shape

- Fonts: `Segoe UI Variable Text, Segoe UI` for text; `Cascadia Mono, Consolas` for code and commands. Sizes: caption 12, body 14, subtitle 16, title 22, display 28. Titles are semi-bold.
- 4-point spacing grid: 4, 8, 12, 16, 24, 32. Corner radius: cards 16, controls 10, pills 999.
- Symbols: use plain Unicode that exists in Segoe UI Symbol (✓ ✕ ⚠ ● ▶ ■ ›). Do not invent icon-font codes.

### 5.3 The glass recipe (what "glass" means here)

WPF cannot blur what is behind an arbitrary panel cheaply, so glass is built from **four layers**:

1. **System backdrop (Windows 11 build 22621+):** Mica or Acrylic behind the whole window via `DwmSetWindowAttribute` (see Appendix A). On older Windows, or when "Reduce effects" is on, show a dark gradient (`BgDeep` → `BgMid`) instead.
2. **Blurred orbs:** a few large, soft, colored ellipses (violet, gold, teal, low opacity) with `BlurEffect Radius=90..120`, placed behind the content in a non-hit-testable `Canvas`. They make the translucent panels look frosted.
3. **Glass panels:** `Border` with a diagonal white gradient fill (about 12 % to 4 %), a 1 px `GlassBorder`, radius 16, and a soft shadow only on a few big surfaces.
4. **Motion:** short fades and slides (150 to 250 ms) when feed items appear, hover transitions, a pulsing dot while the agent is busy.

Performance rule: blur and shadow effects only on the orb layer and on at most 3 to 4 large surfaces. A **Reduce effects** setting removes orbs, shadows and animations.

### 5.4 Screens (wireframes)

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ ◆ KeylessAgent                          [ Ask ▾ ]  ● Chrome connected  ─ ▢ ✕ │  title bar (48 px, draggable)
├────────┬─────────────────────────────────────────────────────────────────────┤
│  ▶     │  Workspace: G:\Projects\MyApp        ⎇ agent/2026…   [Change…]      │  header (glass)
│ Agent  ├───────────────────────────────────────────────────┬─────────────────┤
│  ▣     │  ┌ you ─────────────────────────────┐             │  Changes        │
│ Work-  │  │ create a folder named y          │             │  ✚ y/…          │
│ space  │  └──────────────────────────────────┘             │  ✎ README.md    │
│  ✚     │  › Connecting to the chat…  ✓                      │                 │
│ Doctor │  › Waiting for reply… ●                            │  Summary        │
│  ⚙     │  ┌ model ───────────────────────────┐             │  Branch, turn,  │
│ Settings│ │ I will create the folder.        │             │  commands,      │
│  ⓘ     │  └──────────────────────────────────┘             │  timing         │
│ About  │  ▶ #1 runcommand  mkdir y   ✓ approved             │                 │
│        ├───────────────────────────────────────────────────┴─────────────────┤
│        │  ┌ composer (glass) ────────────────────────────────────────────┐   │
│        │  │ Type your request…                      [ Dry run ☐ ] [ Send ▶]│  │
│        │  └──────────────────────────────────────────────────────────────┘   │
├────────┴─────────────────────────────────────────────────────────────────────┤
│ Ready · Turn 9 · 00:12                                                        │  status bar
└──────────────────────────────────────────────────────────────────────────────┘
```

Dialogs are **overlays inside the window** (dimmed backdrop, centered glass card), never separate windows.

---

## 6. Phases

Each phase ends with: build ok, tests ok, one `@@done` report. The report format is in section 9.

### Phase 1 — Skeleton and solution wiring

**Create**
- `src/UI/ImanSoftware.KeylessAgent.Wpf/ImanSoftware.KeylessAgent.Wpf.csproj` (Appendix B), `App.xaml`, `App.xaml.cs`, `Views/ShellWindow.xaml(.cs)` showing only the text "KeylessAgent".
- `src/tests/ImanSoftware.KeylessAgent.Wpf.Tests/ImanSoftware.KeylessAgent.Wpf.Tests.csproj` with one smoke test (`true.Should().BeTrue()` is not allowed: test that `new ObservableObject()`-like type exists after Phase 4; for now test that the assembly loads: `typeof(App).Assembly.GetName().Name`).
- Add both projects to `KeylessAgent.slnx`.

**Done when:** `dotnet build KeylessAgent.slnx` has 0 errors and 0 warnings, the Core tests still pass, the new test passes.

### Phase 2 — Theme (colors, brushes, glass, controls)

**Create** in `Themes/`: `Colors.xaml`, `Brushes.xaml`, `Typography.xaml`, `Glass.xaml` (styles `GlassCard`, `GlassCardStrong`, `Pill`), `Controls.xaml` (restyled `Button` with variants `PrimaryButton` (gold), `GhostButton`, `DangerButton`; `TextBox`, `ComboBox`, `CheckBox`/toggle switch, `ScrollBar` (thin, rounded), `ToolTip`, `ListBoxItem`), `Animations.xaml` (storyboards `FadeIn`, `SlideUpFadeIn`, `PulseDot`). Merge them in `App.xaml` in this order: Colors, Brushes, Typography, Glass, Controls, Animations. Define brushes **before** the styles that use them.

Also create `Views/StyleGalleryWindow.xaml` (opened only from the About page in Debug builds) that shows every style once, so the user can eyeball the theme.

**Details:** all colors come from the tokens in 5.1. Every interactive control has normal, hover, pressed, focused (gold 2 px ring) and disabled states. Use `ControlTemplate` + `VisualStateManager` or `Triggers`, whichever you can write without errors.

**Tests:** a test that loads every dictionary with `new ResourceDictionary { Source = ... }` is **not** possible without an `Application`; instead write a pure test that every token name in section 5.1 appears in `Colors.xaml` (read the file as text).

### Phase 3 — Window shell, chrome and backdrop

**Create:** `Infrastructure/WindowBackdrop.cs` and `Infrastructure/BackdropPlanner.cs` (Appendix A), the real `ShellWindow.xaml` (custom title bar with minimize/maximize/close buttons, sidebar with five nav items, content area `ContentControl`, status bar), the four layers from 5.3 (fallback gradient, orb canvas, content).

**Rules**
- `WindowStyle="None"`, `Background="Transparent"`, **`AllowsTransparency` must stay false** (it breaks the system backdrop), `WindowChrome` with `GlassFrameThickness="-1"`, `CaptionHeight="48"`, `ResizeBorderThickness="6"`.
- Title-bar buttons need `WindowChrome.IsHitTestVisibleInChrome="True"`.
- When maximized, add padding equal to `SystemParameters.WindowResizeBorderThickness` to the root grid so content is not cut off.
- `BackdropPlanner.Decide(osBuild, preference, reduceEffects)` is a **pure function** returning `{ Backdrop: None|Mica|Acrylic, ShowOrbs, ShowFallbackGradient, Animations }`. Unit test it (Windows 10, build 22000, 22621, reduce effects on/off).
- The backdrop is applied in `OnSourceInitialized` (needs the window handle). If it fails (returns false), show the fallback gradient. It must never throw.

**Done when:** build + tests pass. (Visual check is the user's.)

### Phase 4 — MVVM, navigation, settings service

**Create:** `Mvvm/ObservableObject.cs` (`SetProperty`, `OnPropertyChanged`), `Mvvm/RelayCommand.cs`, `Mvvm/AsyncRelayCommand.cs` (disables itself while running, exposes `IsRunning`, catches exceptions and reports them through an `Action<Exception>`), `ViewModels/ShellViewModel.cs` (current page, nav items, `NavigateCommand`), `Infrastructure/IUiDispatcher.cs`, `WpfDispatcher.cs`, `Infrastructure/SettingsService.cs`.

`SettingsService` stores `%APPDATA%\KeylessAgent\wpf-settings.json`:
`{ "LastWorkspace": "", "RecentWorkspaces": [], "ReduceEffects": false, "BackdropPreference": "Auto", "DryRunDefault": false, "Window": { "Left":..., "Top":..., "Width":..., "Height":..., "Maximized": false } }`.
Use `System.Text.Json`, tolerate a missing or corrupt file (return defaults, never throw), keep at most 10 recent workspaces, take the folder as a constructor argument so tests use a temp folder.

**Tests:** commands (can-execute, async running state, exception path), navigation, settings (missing file, corrupt file, recent list order and limit, round trip).

### Phase 5 — `WpfUserInterface` and the dialog host

**Create:** `Services/IDialogService.cs`, `Services/DialogService.cs`, `ViewModels/DialogHostViewModel.cs`, `ViewModels/Dialogs/ConfirmDialogViewModel.cs`, `ChoiceDialogViewModel.cs`, `AskDialogViewModel.cs`, the three dialog views (as `DataTemplate`s selected by view-model type), `Services/WpfUserInterface.cs` (skeleton in Appendix C), and the overlay in `ShellWindow` (a `Grid` with a dimmed layer and a centered glass card, visible when `DialogHost.Current != null`).

**Behavior**
- Confirm: title, details in a monospace box (selectable, scrollable), buttons **Approve** (gold, default, Enter) and **Decline** (ghost, Esc).
- Choice: title, numbered options (`1  Label` with the description underneath in muted text), the default option highlighted, up/down arrows to move, **Enter** to confirm, **digit keys 1 to 9 pick immediately**, **Esc** cancels (returns `ChoiceResult(null, true)`).
- Ask: question, multiline text box, **Ctrl+Enter** to submit, Esc returns an empty string.
- One dialog at a time (queue). `CancelAll()` completes every pending dialog (Confirm → false, Choice → cancelled, Ask → empty).
- Every dialog fades in (150 ms).

**Tests (with `ImmediateDispatcher`):** two concurrent `ConfirmAsync` calls are shown one after another; answering completes the task; `CancelAll` completes pending tasks; digit key selects the option; Esc cancels; `BeginWait` nesting shows/hides messages in the right order; `Say`/`ShowProgress` from a background thread end up in the feed.

### Phase 6 — Agent page: feed, composer, running a turn

**Create:** `ViewModels/AgentViewModel.cs`, `ViewModels/Feed/*` (item types: `UserRequestItem`, `StepItem`, `ModelMessageItem`, `CommandItem`, `DiffItem`, `ErrorItem`, `TurnSummaryItem`), `Services/EventPresenter.cs`, `Services/AgentRunner.cs`, `Views/AgentView.xaml`, feed item views.

**EventPresenter** (pure, unit tested) maps `AgentEventType` to `{ Label, Symbol, Kind }`:

| Event | Label | Symbol |
|---|---|---|
| SessionStarting | Preparing the session | ● |
| ConnectingToBrowser | Connecting to Chrome | ● |
| OpeningChat | Opening the chat | ● |
| UploadingAttachments | Uploading project snapshot | ▲ |
| SendingMessage | Sending your request | ▶ |
| WaitingForReply | Waiting for the model | ● (pulsing) |
| ValidatingReply | Checking the reply | ✓ |
| RequestingResend | Asking the model to fix its reply | ⚠ |
| ExecutingCommand | Running a command | ▶ |
| AskingUser | Waiting for you | ● |
| SendingResults | Sending results to the model | ▶ |
| RefreshingSnapshot | Updating the project snapshot | ● |
| TurnFinished | Finished | ✓ |

For `ExecutingCommand` the `Detail` looks like `#1 RunCommand mkdir y`: parse it with a regex into number, kind, target; if it does not match, show the raw text. Do not crash on unexpected text.

**AgentRunner** (the only place that calls the loop): creates the loop (verbose logger factory when the user turned diagnostics on), calls `RunAsync(workspace, request, ui, dryRun, ct)`, maps the result to a `TurnSummaryItem`, handles `OperationCanceledException` ("Cancelled") and any other exception (shows an `ErrorItem` with the message, never crashes the app).

**AgentViewModel:** `RequestText`, `DryRun`, `IsRunning`, `SendCommand` (disabled when empty or running or no workspace), `StopCommand` (cancels the token and calls `DialogHost.CancelAll()`), `Feed` (`ObservableCollection<FeedItem>`), `ChangedFiles`, `LastSummary`. Sending adds a `UserRequestItem`, clears the box, and scrolls the feed to the end (use a behavior or a small attached property, not business logic in code-behind). **Ctrl+Enter** sends. Feed items animate in (`SlideUpFadeIn`). The right-hand panel shows the changed files and the summary card; it collapses on narrow windows (< 1100 px).

**Tests:** presenter table, command parsing (valid, invalid, empty), `AgentViewModel` with a **fake `IAgentLoop`** (it needs a small interface or a delegate `Func<...>` injected into `AgentRunner`): successful turn, error turn, cancelled turn, double send blocked.

### Phase 7 — Diff viewer

**Create:** `Services/LineDiff.cs` (Appendix D), `ViewModels/Feed/DiffItem.cs`, `Views/DiffView.xaml`.

- Unified diff with line numbers: removed lines on a red-tinted background (`DiffRemove`), added lines on a green-tinted one (`DiffAdd`), unchanged lines dimmed, long unchanged runs collapsed to `… 42 unchanged lines`. Monospace font. Header shows the path and the kind (Create / Update / Edit / Delete) as a colored pill.
- `FileChange` with `OldContent == null` is a new file (all lines added); `NewContent == null` is a delete (all lines removed). Files above 3000 lines: show "File too large to diff (N lines)" plus counts.
- The diff card is collapsible (default: collapsed after the turn ends, expanded while it is pending confirmation).

**Tests (the important ones):** identical text, empty to text, text to empty, one line changed, line added in the middle, CRLF versus LF (must not show every line as changed), a trailing newline difference, a 5000-line file (returns the "too large" result quickly).

### Phase 8 — Workspace page and header

**Create:** `ViewModels/WorkspaceViewModel.cs`, `Views/WorkspaceView.xaml`, header controls in `ShellWindow` / `AgentView`.

- **Browse…** uses `Microsoft.Win32.OpenFolderDialog` (built into WPF on .NET 8+). Do not use WinForms.
- Recent workspaces as glass cards (name, path, last used); click to select; a remove (✕) button per card.
- Show capabilities from `IWorkspaceInspector` (git installed? is a repository? current branch? agent branch?). Read the inspector's interface and call it on a background task. Failure shows a friendly note, never an exception.
- The header on the Agent page shows the workspace path (trimmed in the middle with a tooltip) and a branch pill.
- A request can only be sent when a valid folder is selected.

**Tests:** recent list handling, invalid path handling, capabilities mapping with a fake inspector.

### Phase 9 — Doctor, undo, Chrome, settings, about

- **Doctor page:** button "Run checks" calls `IChatDiagnostics` exactly like `DoctorCommand`. Show each `SelectorCheck` as a row with a status chip (Matched = Success color, NotFound = Danger, NotApplicableNow = muted) and the detail text; show the summary values (logged in, DeepThink/Search state, input accepts text). Runs in the background with a busy indicator and a Cancel button.
- **Undo:** a button in the Changes panel calls `IBackupService.UndoLatestAsync`, after a Confirm dialog ("This restores the latest backups. Changes made by commands cannot be undone."). Show the list of restored files.
- **Start Chrome:** a button that does what `keyless chrome` does (read `ChromeCommand.cs`); show the connection kind in the status bar.
- **Settings page:** show the loaded `keyless.json` options (read-only, grouped: Mode, Browser, Limits, Snapshot, Git, Commands) from `IConfigurationService.LoadAsync`; buttons **Open keyless.json** (`Process.Start` with `UseShellExecute = true`) and **Reload**. UI-only settings: Reduce effects, Backdrop (Auto/Mica/Acrylic/None), Dry run by default, Verbose diagnostics.
- **About page:** name, version (from the assembly), a short description, the "not affiliated with DeepSeek" notice, the terms-of-service/account-risk warning from the main README, a link-style button that opens the repository folder.

**Tests:** doctor row mapping, settings grouping, undo flow with fake services (confirm declined = nothing called).

### Phase 10 — Polish

- Entrance animations for pages, hover and pressed animations for cards and buttons, a pulsing busy dot while `IsRunning`, smooth feed auto-scroll. All of it is skipped when `ReduceEffects` is on.
- Keyboard: **Ctrl+Enter** send, **Esc** stop the turn (if no dialog is open), **Ctrl+L** focus the composer, **Ctrl+,** open Settings, **F1** open About.
- **Persian/Arabic support in the composer:** `Infrastructure/TextDirection.cs` with a pure function `Detect(string)` that returns RightToLeft when the first strong letter is in the Arabic/Persian ranges (U+0600 to U+06FF, U+0750 to U+077F, U+FB50 to U+FDFF, U+FE70 to U+FEFF) and LeftToRight otherwise. Bind the composer `FlowDirection` and text alignment to it. Unit tests for English, Persian, mixed, empty, digits only.
- Remember window size, position and state (from `SettingsService`), restore them on start, and make sure the restored position is on a visible monitor.
- Global exception handling (Appendix E) writes a crash log to `%LOCALAPPDATA%\KeylessAgent\wpf-crash.log` and shows an error dialog instead of closing silently.
- Optional light theme: only if time remains. Tokens must be `DynamicResource` so a second dictionary can replace them.

### Phase 11 — Packaging

- `dotnet publish src/UI/ImanSoftware.KeylessAgent.Wpf -c Release -r win-x64 --self-contained false -o artifacts/wpf` must succeed. Check that the Playwright driver files (`.playwright` folder) are present in the output (Core depends on Microsoft.Playwright); if not, report it.
- Add `ApplicationIcon` (reuse the CLI `icon.png`; convert to `.ico` only if you can do it with a command that exists; otherwise skip the icon and report it).
- Add `src/UI/ImanSoftware.KeylessAgent.Wpf/publish-wpf.ps1` that runs the command above. Do not run `publish.ps1` of the CLI and do not push anything to NuGet.

### Phase 12 — Documentation (once, last)

Update `README.md` (new section "Desktop app (WPF)": features, how to build and run, requirements, screenshots placeholder, Windows 11 backdrop note, known limitations), add `docs/wpf-ui.md` (architecture, threading model, how to add a page, theme tokens, testing), update the repository layout section, apply and delete `DOCS-TODO.md`. Add a unit test that checks every theme token documented in `docs/wpf-ui.md` exists in `Colors.xaml`.

---

## 7. Testing strategy

- Project `ImanSoftware.KeylessAgent.Wpf.Tests` (`net10.0-windows`, `UseWPF` only if a test really needs a WPF type). Test **ViewModels, services, pure helpers** (`LineDiff`, `EventPresenter`, `BackdropPlanner`, `TextDirection`, `SettingsService`, dialog queue). Do not test pixels.
- Use fakes, not mocking libraries: `FakeAgentLoop`, `FakeDialogService`, `ImmediateDispatcher`, `FakeWorkspaceInspector`.
- Run `dotnet test` for the **whole solution** at the end of every phase to prove you did not break Core or the CLI.
- A test that touches the real Chrome, the network or the real `%APPDATA%` is not allowed (use temp folders).

## 8. Definition of done and the user's manual check

When all phases are done, tell the user to verify by eye (you cannot):

1. The window opens with a Mica/Acrylic or gradient background and soft glowing orbs; panels look frosted.
2. Title-bar buttons work, the window resizes, maximizing does not cut the content.
3. Choosing a workspace, typing a request and pressing Send shows the live feed; Stop really stops.
4. A command from the model opens the Approve/Decline dialog; digit keys work in the choice dialog.
5. Diffs show red and green lines; CRLF files are not shown as fully changed.
6. Doctor shows green/red chips; Undo asks for confirmation.
7. Reduce effects removes orbs/animations; Persian text in the composer is right-to-left.
8. Closing the app leaves no stray Chrome or background process that the app started itself.

## 9. How to report (use exactly this after every phase)

```text
Phase N: done | blocked
Files created/changed: <one bullet each, short>
Build: <command> -> <result>
Tests: <command> -> <passed/failed counts>
Not verified (you cannot see the UI): <one line>
Questions or risks: <one line, or "none">
```

## 10. Common mistakes to avoid

- A `StaticResource` that is used before it is defined (merge order!) fails at runtime, not at compile time. Keep the order from Phase 2 and prefer `DynamicResource` in views.
- `x:Class` must match the namespace and class name of the code-behind exactly. A duplicate `x:Name` is a compile error.
- Do not set `AllowsTransparency="True"` on the main window. Do not put the real content inside the blurred layer (blur affects children).
- Collection changes must happen on the UI thread; use `IUiDispatcher.Post`.
- Do not hold a dialog open from a background thread without the queue. Always release the `SemaphoreSlim` in `finally`.
- If a build fails 3 times with the same error, stop and report the error text instead of trying random changes.

---

## Appendix A — Backdrop helper (Windows 11 Mica/Acrylic)

```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>Applies the Windows 11 system backdrop and dark title bar to a window.</summary>
internal static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;   // Windows 11 22H2 (build 22621) or newer
    private const int RoundCorners = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>kind: 1 = none, 2 = Mica, 3 = Acrylic. Returns false when it could not be applied.</summary>
    public static bool TryApply(Window window, int kind)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            // Let the DWM draw behind the WPF content.
            var source = HwndSource.FromHwnd(handle);
            if (source?.CompositionTarget is not null)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;

            var dark = 1;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var corners = RoundCorners;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
            var backdrop = kind;
            return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
        }
        catch (Exception)
        {
            return false;   // the fallback gradient will be shown
        }
    }
}
```

Use it in `ShellWindow.OnSourceInitialized`: ask `BackdropPlanner.Decide(Environment.OSVersion.Version.Build, settings.BackdropPreference, settings.ReduceEffects)`, call `TryApply` when the plan says Mica/Acrylic, and set `FallbackBackground.Visibility` to `Visible` when the plan says fallback **or** `TryApply` returned false. These numeric values were written from memory: if the backdrop does not appear on Windows 11, report it instead of guessing.

## Appendix B — Project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AssemblyName>ImanSoftware.KeylessAgent.Wpf</AssemblyName>
    <RootNamespace>ImanSoftware.KeylessAgent.Wpf</RootNamespace>
    <!-- Targeted: generated XAML code and WPF types trigger these analyzer rules. Keep this list short. -->
    <NoWarn>$(NoWarn);CA1812;CA1515</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Core\ImanSoftware.KeylessAgent.Core\ImanSoftware.KeylessAgent.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="ImanSoftware.KeylessAgent.Wpf.Tests" />
  </ItemGroup>
</Project>
```

The test project uses `net10.0-windows` and references the Wpf project. Copy its package references from the existing Core test project (no versions).

## Appendix C — `WpfUserInterface` skeleton

```csharp
internal sealed class WpfUserInterface : IAgentUserInterface
{
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly ActivityFeed _feed;          // owns the ObservableCollection of feed items
    private readonly BusyIndicator _busy;         // stack of wait messages

    public WpfUserInterface(IUiDispatcher ui, IDialogService dialogs, ActivityFeed feed, BusyIndicator busy)
        => (_ui, _dialogs, _feed, _busy) = (ui, dialogs, feed, busy);

    public void Say(string message) => _ui.Post(() => _feed.AddModelMessage(message));
    public void ShowProgress(AgentProgressEvent e) => _ui.Post(() => _feed.AddStep(EventPresenter.Present(e)));
    public void ShowDiff(FileChange change) => _ui.Post(() => _feed.AddDiff(change));

    public Task<bool> ConfirmAsync(ConfirmRequest request) => _dialogs.ConfirmAsync(request);
    public Task<string> AskAsync(string question) => _dialogs.AskAsync(question);
    public Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken ct = default)
        => _dialogs.ChooseAsync(request, ct);

    public IDisposable BeginWait(string message)
    {
        var token = _busy.Push(message);                   // thread-safe, posts to the UI thread inside
        return new ActionDisposable(() => _busy.Pop(token)); // safe to dispose twice
    }
}
```

`DialogService` outline: one `SemaphoreSlim(1,1)`; `ConfirmAsync` does `await _gate.WaitAsync()`, creates a `TaskCompletionSource<bool>`, posts `Host.Show(vm)` to the UI thread, `await tcs.Task`, hides the dialog, releases the gate in `finally`. `ChooseAsync` also registers `ct.Register(() => tcs.TrySetResult(ChoiceResult.CancelledMenu()))`.

## Appendix D — Line diff (simple and safe)

```csharp
internal enum DiffLineKind { Unchanged, Added, Removed }
internal sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber);
internal sealed record DiffResult(IReadOnlyList<DiffLine> Lines, int Added, int Removed, bool TooLarge);

internal static class LineDiff
{
    public const int MaxLines = 3000;

    /// <summary>Compares two texts line by line. CRLF and LF are treated as the same line ending.</summary>
    public static DiffResult Compare(string? oldText, string? newText) { /* see steps below */ }
}
```

Steps: (1) normalize `\r\n` and lone `\r` to `\n`, split on `\n`; a final empty element caused by a trailing newline is dropped on both sides; `null` means an empty file. (2) if either side has more than `MaxLines` lines return `TooLarge = true` with the counts. (3) trim the common prefix and common suffix. (4) on the remaining middle part compute the longest common subsequence with a classic dynamic-programming table (the middle part is small after trimming; cap the table at 3000 x 3000 and fall back to "all removed then all added" above that). (5) walk the table to produce `Unchanged/Added/Removed` lines with old/new line numbers. (6) count added and removed lines.

## Appendix E — Crash handling in `App.xaml.cs`

Subscribe to `DispatcherUnhandledException`, `AppDomain.CurrentDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`. In each handler: write the exception (`ToString()`) with a timestamp to `%LOCALAPPDATA%\KeylessAgent\wpf-crash.log` (never throw from the logger), show a short error through the dialog host when it exists, set `e.Handled = true` for the dispatcher one, and keep running unless the error is fatal.
