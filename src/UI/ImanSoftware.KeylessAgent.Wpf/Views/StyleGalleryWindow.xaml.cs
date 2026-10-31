using System.Windows;

namespace ImanSoftware.KeylessAgent.Wpf.Views;

/// <summary>
/// Debug-only window that renders every themed style once, so the user can eyeball the
/// "Midnight &amp; Champagne" theme. Opened only from the About page in debug builds.
/// </summary>
public partial class StyleGalleryWindow : Window
{
    /// <summary>Creates the style gallery window.</summary>
    public StyleGalleryWindow()
    {
        InitializeComponent();
    }
}
