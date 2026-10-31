# DOCS-TODO

- Fixed the startup XamlParseException "Cannot find resource named 'FadeInStoryboard'" (StaticResource forward reference in Themes/Animations.xaml) and added the ThemeOrderTests regression test that walks every Themes/*.xaml dictionary in App.xaml merge order — the StaticResource-order rule is documented in docs/wpf-ui.md under "Common mistakes"; mention that rule and the regression test in the next documentation pass.
