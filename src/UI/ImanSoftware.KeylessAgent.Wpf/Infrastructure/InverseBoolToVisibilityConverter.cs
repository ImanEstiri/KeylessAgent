using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>
/// Converts a bool to its INVERTED visibility: true → Collapsed, false → Visible. Used for
/// empty-state text that shows while a collection or flag is empty.
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
