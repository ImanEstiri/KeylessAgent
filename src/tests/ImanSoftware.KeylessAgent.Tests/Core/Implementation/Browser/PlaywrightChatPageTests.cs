using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// The send-button disable state is parsed from the evaluated JSON — valid and invalid
/// inputs must behave predictably (invalid JSON reads as NOT disabled).
/// </summary>
public class PlaywrightChatPageTests
{
    [Theory]
    [InlineData("{\"d\":true}", true)]
    [InlineData("{\"d\":false}", false)]
    [InlineData(null, false)]
    [InlineData("not json", false)]
    public void IsDisabledFromJson_ParsesTheDisableState(string? disabledJson, bool expected)
    {
        PlaywrightChatPage.IsDisabledFromJson(disabledJson).Should().Be(expected);
    }
}
