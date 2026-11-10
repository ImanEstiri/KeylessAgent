
















using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

public class InterActionDelayResolverTests
{
    private static InterActionDelaysOptions Defaults() => new();

    [Fact]
    public void Disabled_ReturnsZeroForEveryValue()
    {
        var options = new InterActionDelaysOptions { Enabled = false };

        foreach (var value in new[] { -5, 0, 3, 300, 301 })
            InterActionDelayResolver.Resolve(value, options).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Zero_IsPerFieldOptOut()
    {
        InterActionDelayResolver.Resolve(0, Defaults()).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void ValidValue_IsUsedAsSeconds()
    {
        InterActionDelayResolver.Resolve(3, Defaults()).Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Negative_FallsBackToTheInvalidValueFallback()
    {
        InterActionDelayResolver.Resolve(-1, Defaults()).Should().Be(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public void AboveHardCeiling_FallsBack()
    {
        InterActionDelayResolver.Resolve(301, Defaults()).Should().Be(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public void AtCeiling_IsValid()
    {
        InterActionDelayResolver.Resolve(300, Defaults()).Should().Be(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void OverriddenCeiling_IsHonoured()
    {
        var options = new InterActionDelaysOptions { HardCeilingSeconds = 10 };

        InterActionDelayResolver.Resolve(11, options).Should().Be(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public void OverriddenFallback_IsUsed()
    {
        var options = new InterActionDelaysOptions { InvalidValueFallbackSeconds = 7 };

        InterActionDelayResolver.Resolve(-1, options).Should().Be(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public void Defaults_AreAsDocumented()
    {
        var delays = new BrowserOptions().InterActionDelays;

        delays.Enabled.Should().BeTrue();
        delays.AfterExecutionSeconds.Should().Be(2);
        delays.AfterUploadSeconds.Should().Be(2);
        delays.AfterPasteSeconds.Should().Be(2);
        delays.InvalidValueFallbackSeconds.Should().Be(50);
        delays.HardCeilingSeconds.Should().Be(300);
    }

    [Fact]
    public void PhaseHelpers_ReadTheirOwnFields()
    {
        var browser = new BrowserOptions
        {
            InterActionDelays = new InterActionDelaysOptions
            {
                AfterExecutionSeconds = 1,
                AfterUploadSeconds = 2,
                AfterPasteSeconds = 3,
            },
        };

        InterActionDelayResolver.AfterExecution(browser).Should().Be(TimeSpan.FromSeconds(1));
        InterActionDelayResolver.AfterUpload(browser).Should().Be(TimeSpan.FromSeconds(2));
        InterActionDelayResolver.AfterPaste(browser).Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void ShouldHideWindowOnConnect_OnlyForAJustLaunchedChrome()
    {
        var browser = new BrowserOptions();

        PlaywrightChatProvider.ShouldHideWindowOnConnect(BrowserConnectionKind.AutoLaunchedChrome, browser).Should().BeTrue();
        PlaywrightChatProvider.ShouldHideWindowOnConnect(BrowserConnectionKind.PlaywrightLaunched, browser).Should().BeTrue();
        PlaywrightChatProvider.ShouldHideWindowOnConnect(BrowserConnectionKind.AttachedToRunningChrome, browser)
            .Should().BeFalse("turn 2+ leaves the window where the previous turn put it");
    }

    [Fact]
    public void ShouldHideWindowOnConnect_HonoursMinimizeAndNormal()
    {
        PlaywrightChatProvider.ShouldHideWindowOnConnect(
            BrowserConnectionKind.AutoLaunchedChrome,
            new BrowserOptions { MinimizeAfterLogin = false }).Should().BeFalse();

        PlaywrightChatProvider.ShouldHideWindowOnConnect(
            BrowserConnectionKind.AutoLaunchedChrome,
            new BrowserOptions { WindowMode = "Normal" }).Should().BeFalse();
    }
}
