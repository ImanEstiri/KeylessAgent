using System.Windows;
using System.Windows.Controls;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// View of the about page; all behavior lives in <c>AboutViewModel</c>. The style gallery
/// button appears only in debug builds (guide Phase 2).
/// </summary>
public partial class AboutView : UserControl
{
    /// <summary>Creates the about page view; shows the style gallery button in debug builds.</summary>
    public AboutView()
    {
        InitializeComponent();
#if DEBUG
        GalleryButton.Visibility = Visibility.Visible;
#endif
    }

    private void OnOpenGalleryClick(object sender, RoutedEventArgs e)
        => new StyleGalleryWindow().Show();
}
