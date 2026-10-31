using System.Windows;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// The main window of the desktop UI. Phase 1 ships an empty placeholder; navigation,
/// chrome and theming arrive in later phases.
/// </summary>
public partial class ShellWindow : Window
{
    /// <summary>Creates the shell window with its placeholder content.</summary>
    public ShellWindow()
    {
        InitializeComponent();
    }
}
