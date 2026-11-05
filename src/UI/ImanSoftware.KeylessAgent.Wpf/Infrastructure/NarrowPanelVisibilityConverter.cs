using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>
/// Converts the window's ActualWidth to the visibility of the side panel: the panel collapses
/// on narrow windows (below 1100 px, guide Phase 6) and is visible otherwise.
/// </summary>
public sealed class NarrowPanelVisibilityConverter : IValueConverter
{
    /// <summary>Window widths below this are considered narrow.</summary>
    public const double NarrowThreshold = 1100;

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double width && width < NarrowThreshold ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
