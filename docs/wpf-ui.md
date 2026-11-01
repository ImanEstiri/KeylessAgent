# WPF desktop UI

The desktop app lives in `src/UI/ImanSoftware.KeylessAgent.Wpf` (net10.0-windows, WPF, MVVM).
It hosts the same Core agent loop as the CLI — Core knows nothing about WPF; the UI implements
`IAgentUserInterface` and calls `IAgentLoop` through one seam. This document describes what is
actually implemented; the [verification status](#verification-status) section states exactly
what was verified automatically and what still needs a human eye.

## Projects

| Project | Purpose |
|---|---|
| `src/UI/ImanSoftware.KeylessAgent.Wpf` | The app (`WinExe`, `UseWPF`). References only `ImanSoftware.KeylessAgent.Core`; `InternalsVisibleTo` exposes internals to the test project. |
| `src/tests/ImanSoftware.KeylessAgent.Wpf.Tests` | xunit + FluentAssertions tests on net10.0-windows. Uses hand-written fakes (no mocking library). |

Both projects are part of `KeylessAgent.slnx`.

## Architecture

```text
ImanSoftware.KeylessAgent.Wpf/
├── Mvvm/            # ObservableObject, RelayCommand, RelayCommand<T>, AsyncRelayCommand
├── ViewModels/      # one view model per page + DialogHostViewModel + ActivityFeed/BusyIndicator
│   ├── Feed/        # IFeedItem records (UserRequest, Step, Command, Diff, Error, TurnSummary, …)
│   └── Dialogs/     # Confirm/Choice/Ask/Error dialog view models
├── Services/        # IDialogService/DialogService, DialogHost, WpfUserInterface, AgentRunner,
│                    # AgentLoopFactory, EventPresenter, LineDiff, DoctorRowMapper, SettingsGroupMapper
├── Infrastructure/  # IUiDispatcher/WpfDispatcher, SettingsService/WpfSettings, BackdropPlanner,
│                    # WindowBackdrop, TextDirection, WindowPlacement, converters, behaviors
├── Views/           # ShellWindow, one view per page, dialog views, DiffView
└── Themes/          # Colors, Brushes, Typography, Glass, Controls, Controls.Inputs,
                     # Dialogs, Pages, FeedTemplates, Converters, Animations (merged in this order)
```

`ShellWindow` is the composition root: its constructor creates the settings service, the dialog
service (`DialogHost.Initialize`), the `AgentRunner` (with `AgentLoopFactory` as the only loop
factory), every page view model, and a `ShellViewModel` whose page factory maps a `PageKey`
(Agent, Workspace, Doctor, Settings, About) to its view model. Pages render through
`DataTemplate`s selected by view-model type (`Themes/Pages.xaml`); the sidebar binds to the
shell's nav items.

## Threading model

Core calls `IAgentUserInterface` from any thread. The WPF side guarantees UI-thread safety:

- `Say`/`ShowProgress`/`ShowDiff` are posted to the UI thread through `IUiDispatcher`
  (`WpfDispatcher` in the app, an inline fake in tests) before touching the
  `ObservableCollection` in `ActivityFeed`.
- `ConfirmAsync`/`AskAsync`/`ChooseAsync` go through `DialogService`: a `SemaphoreSlim(1,1)`
  serializes dialogs, each request creates its `TaskCompletionSource` **before** acquiring the
  gate (so `CancelAll` also completes queued requests), and all sources use
  `TaskCreationOptions.RunContinuationsAsynchronously` to avoid deadlocks. The gate is always
  released in `finally`; the overlay is always hidden afterwards.
- Stopping a turn cancels the run's `CancellationTokenSource` and calls `DialogHost.CancelAll()`
  (confirm → `false`, choice → cancelled, ask → empty).
- `BeginWait` pushes onto the `BusyIndicator` nested stack (newest message wins, double dispose
  is safe); its change notifications are marshalled to the UI thread.

## How to add a page

1. Add a value to `ViewModels/PageKey`.
2. Add a `NavigationItemViewModel` entry (title + glyph) to `ShellViewModel`.
3. Create the view model in `ViewModels` and the view in `Views`.
4. Add a `DataTemplate` mapping the view model to the view in `Themes/Pages.xaml`.
5. Register the page in the factory lambda in `ShellWindow`'s constructor.

## How to add a feed item

Add an `IFeedItem` record in `ViewModels/Feed/FeedItems.cs` (or `DiffItem.cs`), produce it from
`ActivityFeed` (or the service that owns the event), and give it a `DataTemplate` in
`Themes/FeedTemplates.xaml`.

## Dialogs

One dialog at a time; further requests queue. Keyboard: **Enter** approves a confirmation,
**Esc** declines/cancels; choice menus move with **↑/↓**, confirm with **Enter**, pick
immediately with **1–9**, cancel with **Esc**; ask dialogs submit with **Ctrl+Enter** and cancel
with **Esc** (empty answer); the error dialog closes with **Enter/Esc**. `DialogHost.CancelAll()`
completes every pending dialog whenever the user stops a turn.

## Keyboard shortcuts

| Keys | Action |
|---|---|
| `Ctrl+Enter` | Send the composer text / submit an ask dialog |
| `Esc` | Stop the running turn (only when no dialog is open) |
| `Ctrl+L` | Focus the composer (Agent page) |
| `Ctrl+,` | Open the Settings page |
| `F1` | Open the About page |

## Theme tokens

All colors come from `Themes/Colors.xaml`, mapped to brushes in `Themes/Brushes.xaml`. The unit
test `ThemeTokenTests.DocumentedThemeTokens_ExistInColorsXaml` parses this section — one token
per bullet — and asserts each key exists:

- `BgDeep` — deepest tone of the fallback background gradient.
- `BgMid` — lighter tone of the fallback background gradient.
- `GlassFill` — translucent card fill.
- `GlassFillHover` — translucent fill on hover.
- `GlassFillStrong` — stronger translucent fill (dialogs, active rows, composer cards).
- `GlassBorder` — 1 px hairline for glass surfaces.
- `TextPrimary` — primary text.
- `TextSecondary` — secondary text.
- `TextMuted` — muted captions and hints.
- `Gold` — champagne accent (primary buttons, active navigation, highlights).
- `GoldHover` — gold on hover.
- `GoldPressed` — gold when pressed.
- `Violet` — midnight accent (backdrop orb).
- `Success` — healthy states.
- `Warning` — warnings and notes.
- `Danger` — destructive actions and failures.
- `Info` — informational highlights.
- `DiffAdd` — green tint for added lines.
- `DiffRemove` — red tint for removed lines.

Dictionaries merge in `App.xaml` in this order: Colors, Brushes, Typography, Converters, Glass,
Controls, Controls.Inputs, Dialogs, Pages, FeedTemplates, Animations. Views reference resources
with `DynamicResource` so the order only matters for StaticResource-based styles.
`Views/StyleGalleryWindow.xaml` renders every style once and opens from the About page in
debug builds only.

**Composer pace toggle:** the agent composer has a "Step by step" switch (`AgentViewModel.StepByStep`) that maps to `AgentPace.Step` when on and `AgentPace.Balanced` when off, passed as the turn's pace override.

**TextMuted contrast:** `TextMuted` (#8F98B5) was lightened from #6F7898 so it passes WCAG AA on the glass surfaces: 6.0:1 on regular glass (GlassFill over BgDeep ≈ #171A24) and 4.6:1 on the strongest surface (GlassFillStrong ≈ #2D2F39), up from 3.96:1. Cards are spaced 8 px apart, all buttons are ≥ 32 px high with visible gold focus rings (including the toggle, ComboBox and ListBoxItem), the feed and changes panel have empty states ("Type a request to start" / "No changes yet"), and the title-bar icon buttons carry tooltips.

### Backdrop

`Infrastructure/BackdropPlanner` is a pure function of (OS build, preference, reduce effects):
Windows 11 22H2 (build 22621+) gets the requested system backdrop (Auto/Mica → Mica,
Acrylic → Acrylic, None → none); older builds and failures fall back to the app-drawn gradient
with blurred orbs (orbs hidden when the backdrop applies). The backdrop is applied in
`ShellWindow.OnSourceInitialized` and never throws.

### Reduce effects

The Settings page toggle persists `ReduceEffects` in `wpf-settings.json`. When set, the shell
removes the `Animations` resource dictionary (so `FadeIn`, `SlideUpFadeIn` and `PulseDot` styles
no-op) and the backdrop planner switches to the plain gradient without orbs.

### Text direction

`Infrastructure/TextDirection.Detect` returns RightToLeft when the first strong letter of the
composer text is in the Arabic/Persian ranges (U+0600–06FF, U+0750–077F, U+FB50–FDFF,
U+FE70–FEFF); digits, spaces and punctuation are neutral. Two converters bind it to the
composer's `FlowDirection`/`TextAlignment` — only the composer flips, the rest of the UI stays
left-to-right.

## Window persistence

`%APPDATA%\KeylessAgent\wpf-settings.json` stores `LastWorkspace`, `RecentWorkspaces` (max 10,
newest first), `ReduceEffects`, `BackdropPreference`, `DryRunDefault` and a `Window` block
(`Left`, `Top`, `Width`, `Height`, `Maximized`). On close the shell saves its bounds (restored
bounds when maximized); on start `Infrastructure/WindowPlacement` validates them — sizes under
200 DIP are ignored, and the position is clamped so at least a 100 DIP strip stays visible on
the virtual screen. A missing or corrupt file yields defaults.

## Crash log

Per guide Appendix E, `App.xaml.cs` handles `DispatcherUnhandledException`,
`AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`: each exception is
appended (timestamped, never throwing) to `%LOCALAPPDATA%\KeylessAgent\wpf-crash.log` and a
short error dialog is shown. The app keeps running.

## Testing

`dotnet test src/tests/ImanSoftware.KeylessAgent.Wpf.Tests` — 201 tests at the time of writing.
The suite avoids launching a WPF `Application`:

- View models and services run against the `ImmediateDispatcher` fake and fake Core services
  (`FakeDialogService`, `FakeBackupService`, `FakeConfigurationService`, `FakeBrowserConnector`)
  plus temporary directories — never the real `%APPDATA%` and never Chrome.
- XAML dictionaries are asserted as text (theme tokens, documented tokens).
- Pure logic (`LineDiff`, `EventPresenter`, `DoctorRowMapper`, `SettingsGroupMapper`,
  `TextDirection`, `WindowPlacement`, `BackdropPlanner`) is tested directly.
- Internals (e.g. `LineDiff`, `DiffItem`) are reachable via `InternalsVisibleTo`.

## Publishing

`powershell -File src\UI\ImanSoftware.KeylessAgent.Wpf\publish-wpf.ps1` runs the framework-
dependent publish (`-c Release -r win-x64 --self-contained false`) into `artifacts\wpf`,
verifies the executable, the Playwright driver (`.playwright` folder and
`Microsoft.Playwright.dll`) and the presence of the .NET 10 Desktop Runtime, and exits non-zero
on any failure. The runtime machine needs the **.NET 10 Desktop Runtime**; browser automation
needs the dedicated Chrome profile (see the CLI docs — the desktop app's "Start Chrome" button
does exactly what `keyless chrome` does). The application icon is intentionally not set yet:
`icon.png` exists but no PNG→ICO conversion tool was available.

## Common mistakes

- StaticResource needs the target defined above it — in the same file, or in a dictionary that
  App.xaml merges earlier. `BeginStoryboard.Storyboard` cannot use DynamicResource, so every
  animation Storyboard must be declared before the Style that starts it
  (Themes/Animations.xaml). `ThemeOrderTests` walks all `Themes/*.xaml` dictionaries in merge
  order and fails on any forward reference.
- **Never use DynamicResource inside the theme dictionaries themselves.** A DynamicResource
  setter creates a `ResourceReferenceExpression`; sealing a freshly created deferred resource
  invalidates the expressions it contains, which re-fetches (and re-creates) other deferred
  resources — the re-entrant cascade overflows the stack in `FindResourceInternal` at first
  window show (observed as `StackOverflowException` with the
  `SealValue → AddInheritanceContext → InvalidateExpressionValue → GetValue → CreateObject`
  cycle repeating ~1200 times). Theme dictionaries must be fully resolvable with
  StaticResource — enforced by `ThemeOrderTests.ThemeDictionaries_ContainNoDynamicResourceReferences`.
  Views keep DynamicResource (they resolve against cached entries and stay re-themeable).

## Verification status

- **Automated (verified):** whole-solution build with 0 errors (0 warnings in the WPF projects);
  201 WPF tests + 574 Core/CLI tests green; the publish command and `publish-wpf.ps1` executed
  successfully with the Playwright driver present in the output.
- **Not verified (needs a human):** the app was never launched during development. Visual
  appearance, layout, animations, the Windows 11 backdrop and the folder/file dialogs still
  require manual checks on a real desktop.
