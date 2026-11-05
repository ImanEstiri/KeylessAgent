using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Infrastructure;

/// <summary>
/// The backdrop decision matrix: Windows 10 (no system backdrop), Windows 11 21H2 (build
/// 22000, still unsupported), Windows 11 22H2 (build 22621, Mica/Acrylic), the user's
/// preference and the "Reduce effects" toggle.
/// </summary>
public class BackdropPlannerTests
{
    private const int Windows10Build = 19045;
    private const int Windows11FirstReleaseBuild = 22000;
    private const int Windows11_22H2Build = 22621;

    [Fact]
    public void Windows10_FallsBackToGradientWithOrbs()
    {
        var plan = BackdropPlanner.Decide(Windows10Build, "Auto", reduceEffects: false);

        plan.Backdrop.Should().Be(BackdropKind.None);
        plan.ShowOrbs.Should().BeTrue("the orbs provide the frosted look without a system backdrop");
        plan.ShowFallbackGradient.Should().BeTrue();
        plan.Animations.Should().BeTrue();
    }

    [Fact]
    public void Windows11_21H2_AlsoFallsBackToGradient()
    {
        var plan = BackdropPlanner.Decide(Windows11FirstReleaseBuild, "Auto", reduceEffects: false);

        plan.Backdrop.Should().Be(BackdropKind.None, "the system backdrop needs build 22621");
        plan.ShowOrbs.Should().BeTrue();
        plan.ShowFallbackGradient.Should().BeTrue();
    }

    [Fact]
    public void Windows11_22H2_Auto_PicksMica()
    {
        var plan = BackdropPlanner.Decide(Windows11_22H2Build, "Auto", reduceEffects: false);

        plan.Backdrop.Should().Be(BackdropKind.Mica);
        plan.ShowOrbs.Should().BeTrue();
        plan.ShowFallbackGradient.Should().BeFalse();
        plan.Animations.Should().BeTrue();
    }

    [Fact]
    public void Windows11_22H2_AcrylicPreference_IsHonored()
    {
        var plan = BackdropPlanner.Decide(Windows11_22H2Build, "Acrylic", reduceEffects: false);

        plan.Backdrop.Should().Be(BackdropKind.Acrylic);
    }

    [Fact]
    public void Windows11_22H2_NonePreference_ShowsGradientWithoutOrbs()
    {
        var plan = BackdropPlanner.Decide(Windows11_22H2Build, "None", reduceEffects: false);

        plan.Backdrop.Should().Be(BackdropKind.None);
        plan.ShowOrbs.Should().BeFalse("a user who chose None wants a calm, flat window");
        plan.ShowFallbackGradient.Should().BeTrue();
        plan.Animations.Should().BeTrue();
    }

    [Fact]
    public void ReduceEffects_DisablesBackdropOrbsAndAnimations()
    {
        var plan = BackdropPlanner.Decide(Windows11_22H2Build, "Auto", reduceEffects: true);

        plan.Backdrop.Should().Be(BackdropKind.None);
        plan.ShowOrbs.Should().BeFalse();
        plan.ShowFallbackGradient.Should().BeTrue();
        plan.Animations.Should().BeFalse();
    }

    [Theory]
    [InlineData(Windows10Build)]
    [InlineData(Windows11FirstReleaseBuild)]
    [InlineData(Windows11_22H2Build)]
    public void ReduceEffects_WinsOverEveryBuild(int osBuild)
    {
        var plan = BackdropPlanner.Decide(osBuild, "Mica", reduceEffects: true);

        plan.Backdrop.Should().Be(BackdropKind.None);
        plan.ShowOrbs.Should().BeFalse();
        plan.ShowFallbackGradient.Should().BeTrue();
        plan.Animations.Should().BeFalse();
    }

    [Fact]
    public void UnknownPreference_FallsBackToAuto()
    {
        var plan = BackdropPlanner.Decide(Windows11_22H2Build, "shiny", reduceEffects: false);

        plan.Backdrop.Should().Be(BackdropKind.Mica, "an unrecognized preference behaves like Auto");
    }
}
