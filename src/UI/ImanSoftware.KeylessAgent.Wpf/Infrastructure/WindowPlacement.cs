namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>A validated window placement ready to apply.</summary>
/// <param name="Left">The clamped left edge.</param>
/// <param name="Top">The clamped top edge.</param>
/// <param name="Width">The clamped width.</param>
/// <param name="Height">The clamped height.</param>
/// <param name="Restored">False when the saved bounds were unusable (keep the XAML defaults).</param>
public sealed record RestoredWindowPlacement(double Left, double Top, double Width, double Height, bool Restored);

/// <summary>
/// Pure validation of saved window bounds: restores only usable sizes and clamps the position
/// so at least a visible strip of the window stays on a monitor. No WPF types — unit tested
/// with virtual-screen coordinates passed in.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Sizes below this are treated as unusable.</summary>
    public const double MinimumSize = 200;

    /// <summary>How much of the window must stay visible on a monitor.</summary>
    public const double MinimumVisible = 100;

    /// <summary>Validates saved bounds against the virtual screen.</summary>
    /// <param name="left">Saved left edge (null = not saved).</param>
    /// <param name="top">Saved top edge (null = not saved).</param>
    /// <param name="width">Saved width (null = not saved).</param>
    /// <param name="height">Saved height (null = not saved).</param>
    /// <param name="screenLeft">Virtual screen left edge.</param>
    /// <param name="screenTop">Virtual screen top edge.</param>
    /// <param name="screenRight">Virtual screen right edge.</param>
    /// <param name="screenBottom">Virtual screen bottom edge.</param>
    public static RestoredWindowPlacement Validate(
        double? left, double? top, double? width, double? height,
        double screenLeft, double screenTop, double screenRight, double screenBottom)
    {
        if (width is not double savedWidth || height is not double savedHeight
            || savedWidth < MinimumSize || savedHeight < MinimumSize
            || left is not double savedLeft || top is not double savedTop)
            return new RestoredWindowPlacement(0, 0, 0, 0, Restored: false);

        var clampedWidth = Math.Min(savedWidth, Math.Max(0, screenRight - screenLeft));
        var clampedHeight = Math.Min(savedHeight, Math.Max(0, screenBottom - screenTop));
        var visibleX = Math.Min(MinimumVisible, clampedWidth);
        var visibleY = Math.Min(MinimumVisible, clampedHeight);

        var minLeft = screenLeft - (clampedWidth - visibleX);
        var maxLeft = screenRight - visibleX;
        var minTop = screenTop - (clampedHeight - visibleY);
        var maxTop = screenBottom - visibleY;

        if (maxLeft < minLeft)
        {
            var centered = screenLeft + ((screenRight - screenLeft) - clampedWidth) / 2;
            minLeft = maxLeft = centered;
        }

        if (maxTop < minTop)
        {
            var centered = screenTop + ((screenBottom - screenTop) - clampedHeight) / 2;
            minTop = maxTop = centered;
        }

        return new RestoredWindowPlacement(
            Math.Clamp(savedLeft, minLeft, maxLeft),
            Math.Clamp(savedTop, minTop, maxTop),
            clampedWidth,
            clampedHeight,
            Restored: true);
    }
}
