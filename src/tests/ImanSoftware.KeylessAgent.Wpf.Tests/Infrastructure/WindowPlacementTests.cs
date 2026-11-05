using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Infrastructure;

public sealed class WindowPlacementTests
{
    private const double ScreenLeft = 0;
    private const double ScreenTop = 0;
    private const double ScreenRight = 1920;
    private const double ScreenBottom = 1080;

    [Fact]
    public void ValidBounds_AreKeptUnchanged()
    {
        var placement = WindowPlacement.Validate(100, 80, 1280, 800, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom);

        placement.Restored.Should().BeTrue();
        placement.Left.Should().Be(100);
        placement.Top.Should().Be(80);
        placement.Width.Should().Be(1280);
        placement.Height.Should().Be(800);
    }

    [Fact]
    public void MissingSize_IsNotRestored()
    {
        WindowPlacement.Validate(100, 80, null, 800, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom)
            .Restored.Should().BeFalse();
    }

    [Fact]
    public void TinySize_IsNotRestored()
    {
        WindowPlacement.Validate(100, 80, 100, 800, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom)
            .Restored.Should().BeFalse();
        WindowPlacement.Validate(100, 80, 1280, 10, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom)
            .Restored.Should().BeFalse();
    }

    [Fact]
    public void MissingPosition_IsNotRestored()
    {
        WindowPlacement.Validate(null, 80, 1280, 800, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom)
            .Restored.Should().BeFalse();
        WindowPlacement.Validate(100, null, 1280, 800, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom)
            .Restored.Should().BeFalse();
    }

    [Fact]
    public void FullyOffScreenRight_IsClampedSoAStripStaysVisible()
    {
        var placement = WindowPlacement.Validate(2500, 100, 800, 600, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom);

        placement.Restored.Should().BeTrue();
        placement.Left.Should().Be(ScreenRight - WindowPlacement.MinimumVisible);
    }

    [Fact]
    public void FullyOffScreenBottom_IsClampedSoAStripStaysVisible()
    {
        var placement = WindowPlacement.Validate(100, 2000, 800, 600, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom);

        placement.Top.Should().Be(ScreenBottom - WindowPlacement.MinimumVisible);
    }

    [Fact]
    public void FarOffScreenLeft_IsClampedToTheMinimumOverhang()
    {
        var placement = WindowPlacement.Validate(-1500, 100, 800, 600, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom);

        placement.Left.Should().Be(-(800 - WindowPlacement.MinimumVisible));
    }

    [Fact]
    public void OversizedWindow_IsClampedToTheScreen()
    {
        var placement = WindowPlacement.Validate(100, 100, 3000, 2000, ScreenLeft, ScreenTop, ScreenRight, ScreenBottom);

        placement.Width.Should().Be(ScreenRight - ScreenLeft);
        placement.Height.Should().Be(ScreenBottom - ScreenTop);
        placement.Restored.Should().BeTrue();
    }

    [Fact]
    public void MultiMonitorNegativeCoordinates_AreRespected()
    {
        const double virtualLeft = -1920;

        var placement = WindowPlacement.Validate(-1800, 50, 1280, 800, virtualLeft, 0, ScreenRight, ScreenBottom);

        placement.Restored.Should().BeTrue();
        placement.Left.Should().Be(-1800);
    }
}
