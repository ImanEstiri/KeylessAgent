
















using System.Text.Json;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using Microsoft.Playwright;
using NSubstitute;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

public class ChromeWindowMoverTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static ICDPSession SessionReturning(JsonElement response)
    {
        var session = Substitute.For<ICDPSession>();
        session.SendAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(_ => Task.FromResult<JsonElement?>(response));
        return session;
    }

    private static ICDPSession SessionReturningBounds(int left, int top, int width, int height, string windowState = "normal")
        => SessionReturning(Json(new { bounds = new { left, top, width, height, windowState } }));

    [Fact]
    public async Task ReadBack_ClampedWidthAndHeight_WithPositionInTolerance_IsTrue()
    {
        var session = SessionReturningBounds(1840, 1000, 516, 95);

        var matches = await ChromeWindowMover.ReadBackMatchesAsync(session, 42, (1840, 1000, 80, 80));

        matches.Should().BeTrue();
    }

    [Fact]
    public async Task ReadBack_ExactSize_ButPositionOffByMoreThanTolerance_IsFalse()
    {
        var offBy = ChromeWindowMover.PositionTolerancePx + 1;
        var session = SessionReturningBounds(1840 + offBy, 1000, 80, 80);

        var matches = await ChromeWindowMover.ReadBackMatchesAsync(session, 42, (1840, 1000, 80, 80));

        matches.Should().BeFalse();
    }

    [Theory]
    [InlineData("minimized")]
    [InlineData("maximized")]
    public async Task ReadBack_PositionOnTarget_ButNonNormalState_IsFalse(string windowState)
    {
        var session = SessionReturningBounds(1840, 1000, 80, 80, windowState);

        var matches = await ChromeWindowMover.ReadBackMatchesAsync(session, 42, (1840, 1000, 80, 80));

        matches.Should().BeFalse();
    }

    [Fact]
    public async Task ReadBack_ResponseWithoutBoundsProperty_IsFalse()
    {
        var session = SessionReturning(Json(new { }));

        var matches = await ChromeWindowMover.ReadBackMatchesAsync(session, 42, (1840, 1000, 80, 80));

        matches.Should().BeFalse();
    }
}
