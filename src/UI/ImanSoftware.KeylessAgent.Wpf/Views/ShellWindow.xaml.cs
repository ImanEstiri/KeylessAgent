using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// The main window: custom title bar, sidebar navigation, status bar, and the Windows 11
/// system backdrop with an app-drawn gradient fallback. View-only; logic comes with Phase 4.
/// </summary>
public partial class ShellWindow : Window
{
    /// <summary>Creates the shell window and applies the backdrop plan.</summary>
    public ShellWindow()
    {
        InitializeComponent();
        ApplyBackdropPlan();
    }

    /// <summary>Reacts to maximize/restore so the content is not cut off by the resize border.</summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        RootGrid.Margin = WindowState == WindowState.Maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;
    }

    private void ApplyBackdropPlan()
    {
        // The user preference arrives with Phase 4 (SettingsService); Auto until then.
        var plan = BackdropPlanner.Decide(
            Environment.OSVersion.Version.Build, preference: "Auto", reduceEffects: false);

        OrbCanvas.Visibility = plan.ShowOrbs ? Visibility.Visible : Visibility.Collapsed;
        FallbackBackground.Visibility = plan.ShowFallbackGradient ? Visibility.Visible : Visibility.Collapsed;

        if (plan.Backdrop is not BackdropKind.None)
        {
            var applied = WindowBackdrop.TryApply(this, (int)plan.Backdrop);
            if (!applied)
                FallbackBackground.Visibility = Visibility.Visible;
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
