using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Infrastructure;

public sealed class PathTextTests
{
    [Fact]
    public void ShortPath_IsUnchanged()
    {
        PathText.MiddleTrim(@"G:\demo", maxLength: 48).Should().Be(@"G:\demo");
    }

    [Fact]
    public void PathAtExactMaxLength_IsUnchanged()
    {
        var path = new string('x', 48);

        PathText.MiddleTrim(path, maxLength: 48).Should().Be(path);
    }

    [Fact]
    public void LongPath_IsTrimmedInTheMiddle_KeepingBothEnds()
    {
        var path = @"G:\Projects\Git\Iman\KeylessAgent\src\UI\ImanSoftware.KeylessAgent.Wpf";

        var display = PathText.MiddleTrim(path, maxLength: 24);

        display.Length.Should().Be(23);
        display.Should().Contain("…");
        display.Should().StartWith(path[..11]);
        display.Should().EndWith(path[^11..]);
    }

    [Fact]
    public void NullPath_YieldsEmptyString()
    {
        PathText.MiddleTrim(null).Should().BeEmpty();
    }

    [Fact]
    public void EmptyPath_YieldsEmptyString()
    {
        PathText.MiddleTrim(string.Empty).Should().BeEmpty();
    }
}
