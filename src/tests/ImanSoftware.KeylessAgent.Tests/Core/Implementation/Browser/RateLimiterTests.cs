
















using System.Diagnostics;
using FluentAssertions;
using ImanSoftware.DateTimeProvider;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Configuration;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// The inter-send spacing floor: a minimum wall-clock gap between two consecutive chat
/// messages, added ON TOP of the burst limit and the jitter. This is what stops a burst of
/// quick batches from tripping the provider's anti-abuse captcha.
/// </summary>
public class RateLimiterTests
{
    [Fact]
    public async Task MinSpacing_IsRespectedBetweenTwoSends()
    {
        var limiter = new RateLimiter(1000, 0, 0, TimeSpan.FromMilliseconds(150), new DefaultDateTimeProvider());

        var stopwatch = Stopwatch.StartNew();
        await limiter.WaitAsync();
        await limiter.WaitAsync();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(140));
    }

    [Fact]
    public async Task ZeroSpacing_IsANoOp()
    {
        var limiter = new RateLimiter(1000, 0, 0, TimeSpan.Zero, new DefaultDateTimeProvider());

        var stopwatch = Stopwatch.StartNew();
        await limiter.WaitAsync();
        await limiter.WaitAsync();
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Cancellation_FlowsThroughTheSpacingFloor()
    {
        var limiter = new RateLimiter(1000, 0, 0, TimeSpan.FromSeconds(30), new DefaultDateTimeProvider());
        await limiter.WaitAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var act = async () => await limiter.WaitAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BurstLimit_StillFires_WithNoSpacing()
    {
        // The fake clock never advances: two recorded sends fill the minute, the third is held.
        var limiter = new RateLimiter(2, 0, 0, TimeSpan.Zero, new FakeDateTimeProvider());
        await limiter.WaitAsync();
        await limiter.WaitAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var third = limiter.WaitAsync(cts.Token);

        await Task.Delay(100);
        third.IsCompleted.Should().BeFalse();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);
    }

    [Fact]
    public void NegativeSpacing_IsRejected()
    {
        var act = () => new RateLimiter(10, 0, 0, TimeSpan.FromSeconds(-1), new DefaultDateTimeProvider());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MinMessageSpacingSeconds_DefaultsTo5()
    {
        new KeylessOptions().Browser.MinMessageSpacingSeconds.Should().Be(5);
    }

    [Fact]
    public void Validate_RejectsOutOfRangeSpacing()
    {
        foreach (var spacing in new[] { -1, 301 })
        {
            var options = new KeylessOptions { Browser = new BrowserOptions { MinMessageSpacingSeconds = spacing } };
            ConfigurationService.Validate(options).IsSuccess.Should().BeFalse();
        }
    }

    [Fact]
    public void Validate_AcceptsBoundaryAndDefaultSpacing()
    {
        foreach (var spacing in new[] { 0, 5, 300 })
        {
            var options = new KeylessOptions { Browser = new BrowserOptions { MinMessageSpacingSeconds = spacing } };
            ConfigurationService.Validate(options).IsSuccess.Should().BeTrue();
        }
    }
}
