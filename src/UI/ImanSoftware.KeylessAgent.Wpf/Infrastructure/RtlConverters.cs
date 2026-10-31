using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>Converts composer text to a WPF FlowDirection via <see cref="TextDirection.Detect"/>.</summary>
public sealed class RtlFlowDirectionConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => TextDirection.Detect(value as string) == TextFlowDirection.RightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts composer text to a WPF TextAlignment (Right for RTL, Left for LTR).</summary>
public sealed class RtlTextAlignmentConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => TextDirection.Detect(value as string) == TextFlowDirection.RightToLeft
            ? TextAlignment.Right
            : TextAlignment.Left;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
