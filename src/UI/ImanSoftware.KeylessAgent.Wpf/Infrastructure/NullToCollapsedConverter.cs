using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>Converts null to Collapsed and any non-null value to Visible (optional-value rows).</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
