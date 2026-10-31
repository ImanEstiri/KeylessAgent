# DOCS-TODO

- Fixed the startup `StackOverflowException` in `FindResourceInternal` (the 99 DynamicResource references inside Themes/*.xaml created a re-entrant deferred-resource seal/invalidate cascade at first window show; all theme-internal references are now StaticResource and ThemeOrderTests forbids DynamicResource in theme dictionaries) — update docs/wpf-ui.md's architecture section if a light theme ever reintroduces DynamicResource into the dictionaries, and mention the fix in the release notes.

- Fixed the startup XamlParseException "Cannot find resource named 'FadeInStoryboard'" (StaticResource forward reference in Themes/Animations.xaml) and added the ThemeOrderTests regression test that walks every Themes/*.xaml dictionary in App.xaml merge order — the StaticResource-order rule is documented in docs/wpf-ui.md under "Common mistakes"; mention that rule and the regression test in the next documentation pass.
