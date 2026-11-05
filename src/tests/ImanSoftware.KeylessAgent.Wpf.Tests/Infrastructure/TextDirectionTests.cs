using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Infrastructure;

public sealed class TextDirectionTests
{
    [Theory]
    [InlineData("Hello world")]
    [InlineData("fix the login flow")]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" ... !? ")]
    public void Detect_EnglishNeutralOrDigitOnlyText_IsLeftToRight(string? text)
    {
        TextDirection.Detect(text).Should().Be(TextFlowDirection.LeftToRight);
    }

    [Theory]
    [InlineData("سلام دنیا")]
    [InlineData("سلام world")]
    [InlineData("42 سلام")] // digits are neutral; the first strong letter is Arabic
    [InlineData("  \u06CC\u06A9 test")]
    [InlineData("\u06F1\u06F2")] // Persian digits U+06F1.. fall into U+0600–U+06FF
    public void Detect_PersianOrArabicFirstStrongLetter_IsRightToLeft(string? text)
    {
        TextDirection.Detect(text).Should().Be(TextFlowDirection.RightToLeft);
    }

    [Fact]
    public void Detect_MixedText_FirstStrongLetterDecides()
    {
        // Latin first strong letter despite Arabic later.
        TextDirection.Detect("test سلام").Should().Be(TextFlowDirection.LeftToRight);

        // Arabic first strong letter despite Latin later.
        TextDirection.Detect("سلام test").Should().Be(TextFlowDirection.RightToLeft);
    }

    [Fact]
    public void Detect_ArabicSupplementRanges_AreRecognized()
    {
        // U+0750–U+077F (Arabic Supplement).
        TextDirection.Detect("\u0750").Should().Be(TextFlowDirection.RightToLeft);
        // U+FB50–U+FDFF (Arabic Presentation Forms-A).
        TextDirection.Detect("\uFB50").Should().Be(TextFlowDirection.RightToLeft);
        // U+FE70–U+FEFF (Arabic Presentation Forms-B).
        TextDirection.Detect("\uFE70").Should().Be(TextFlowDirection.RightToLeft);
    }

    [Fact]
    public void Detect_DigitsOnly_IsLeftToRight()
    {
        TextDirection.Detect("1234567890").Should().Be(TextFlowDirection.LeftToRight);
    }
}
