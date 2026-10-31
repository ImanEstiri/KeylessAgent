# DOCS-TODO

## WPF UI (Phase 1)
- Created the WPF desktop project `src/UI/ImanSoftware.KeylessAgent.Wpf` (net10.0-windows, WinExe, UseWPF, project reference to Core, InternalsVisibleTo for its tests) — document it in the repository layout and docs in Phase 12.
- Created the test project `src/tests/ImanSoftware.KeylessAgent.Wpf.Tests` (net10.0-windows, xunit + FluentAssertions, package references without versions via CPM) — document the testing strategy in Phase 12.
- Added both projects to `KeylessAgent.slnx` — mention the desktop app in the README in Phase 12.
- Phase 2 theme dictionaries created (Themes/Colors.xaml, Brushes.xaml, Typography.xaml, Glass.xaml, Controls.xaml, Controls.Inputs.xaml, Animations.xaml) — document the tokens and merge order in docs/wpf-ui.md in Phase 12.
- Phase 2 style gallery window created (Views/StyleGalleryWindow.xaml, debug-only from About in Phase 9) — mention it in Phase 12 docs.
- Theme token test (ThemeTokenTests) pins guide 5.1 tokens — keep it in sync when tokens change.
- Phase 3 shell chrome/backdrop created (custom title bar, sidebar, status bar, WindowChrome, WindowBackdrop + BackdropPlanner with fallback) — document the backdrop decision matrix and window rules in docs/wpf-ui.md in Phase 12.
- BackdropPlannerTests pin the backdrop decision matrix (Windows 10 / 22000 / 22621, preferences, reduce effects).
- Phase 4 MVVM infrastructure created (Mvvm/ObservableObject, RelayCommand, RelayCommand&lt;T&gt;, AsyncRelayCommand with IsRunning + IsRunningChanged + onError callback) — document the MVVM conventions in docs/wpf-ui.md in Phase 12.
- Phase 4 dispatcher abstraction created (Infrastructure/IUiDispatcher, WpfDispatcher; ImmediateDispatcher fake lives in the test project) — document the threading model (Post/InvokeAsync, why tests need no WPF Application) in Phase 12.
- Phase 4 settings service created (Infrastructure/SettingsService + WpfSettings/WindowSettings) persisting %APPDATA%\KeylessAgent\wpf-settings.json with corrupt-file fallback and a 10-entry recent-workspace list — document the file and schema in docs/wpf-ui.md in Phase 12.
- Phase 4 navigation created (ViewModels/PageKey, NavigationItemViewModel, ShellViewModel with NavigateCommand and optional page factory) — document how to add a page in docs/wpf-ui.md in Phase 12.
