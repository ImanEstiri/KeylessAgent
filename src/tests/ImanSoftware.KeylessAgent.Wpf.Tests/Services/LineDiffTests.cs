using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Services;
using Xunit;
using DiffLineKind = ImanSoftware.KeylessAgent.Wpf.Services.DiffLineKind;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Services;

public sealed class LineDiffTests
{
    [Fact]
    public void IdenticalTexts_AreAllUnchanged()
    {
        var result = LineDiff.Compare("a\nb\nc", "a\nb\nc");

        result.TooLarge.Should().BeFalse();
        result.Added.Should().Be(0);
        result.Removed.Should().Be(0);
        result.Lines.Select(line => line.Kind).Should().OnlyContain(kind => kind == DiffLineKind.Unchanged);
        result.Lines.Select(line => line.Text).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void EmptyToText_IsAllAdded()
    {
        var result = LineDiff.Compare("", "a\nb");

        result.TooLarge.Should().BeFalse();
        result.Added.Should().Be(2);
        result.Removed.Should().Be(0);
        result.Lines.Should().HaveCount(2);
        result.Lines.Should().OnlyContain(line => line.Kind == DiffLineKind.Added);
        result.Lines.Select(line => line.NewNumber).Should().Equal(1, 2);
        result.Lines.Should().OnlyContain(line => line.OldNumber == null);
    }

    [Fact]
    public void TextToEmpty_IsAllRemoved()
    {
        var result = LineDiff.Compare("a\nb", "");

        result.Added.Should().Be(0);
        result.Removed.Should().Be(2);
        result.Lines.Should().OnlyContain(line => line.Kind == DiffLineKind.Removed);
        result.Lines.Select(line => line.OldNumber).Should().Equal(1, 2);
    }

    [Fact]
    public void NullMeansAnEmptyFile()
    {
        var result = LineDiff.Compare(null, "a");

        result.Lines.Should().ContainSingle();
        result.Lines[0].Kind.Should().Be(DiffLineKind.Added);
    }

    [Fact]
    public void BothNull_ProducesNoLines()
    {
        var result = LineDiff.Compare(null, null);

        result.Lines.Should().BeEmpty();
        result.Added.Should().Be(0);
        result.Removed.Should().Be(0);
    }

    [Fact]
    public void OneChangedLine_IsOneRemovedPlusOneAdded()
    {
        var result = LineDiff.Compare("a\nold\nb", "a\nnew\nb");

        var middle = result.Lines.Skip(1).Take(2).ToList();
        middle[0].Kind.Should().Be(DiffLineKind.Removed);
        middle[0].Text.Should().Be("old");
        middle[0].OldNumber.Should().Be(2);
        middle[1].Kind.Should().Be(DiffLineKind.Added);
        middle[1].Text.Should().Be("new");
        middle[1].NewNumber.Should().Be(2);
        result.Added.Should().Be(1);
        result.Removed.Should().Be(1);
    }

    [Fact]
    public void LineAddedInTheMiddle_KeepsSurroundingNumbersCorrect()
    {
        var result = LineDiff.Compare("a\nb", "a\nx\nb");

        result.Lines.Select(line => line.Kind).Should().Equal(
            DiffLineKind.Unchanged, DiffLineKind.Added, DiffLineKind.Unchanged);
        result.Lines[1].Text.Should().Be("x");
        result.Lines[1].NewNumber.Should().Be(2);
        result.Lines[2].OldNumber.Should().Be(2);
        result.Lines[2].NewNumber.Should().Be(3);
        result.Added.Should().Be(1);
        result.Removed.Should().Be(0);
    }

    [Fact]
    public void ReplacedBlock_GroupsRemovedThenAdded()
    {
        var result = LineDiff.Compare("a\nb\nc\nd", "a\nx\nd");

        result.Lines.Skip(1).Take(3).Select(line => line.Kind).Should().Equal(
            DiffLineKind.Removed, DiffLineKind.Removed, DiffLineKind.Added);
        result.Added.Should().Be(1);
        result.Removed.Should().Be(2);
    }

    [Fact]
    public void CrlfVersusLf_IsNotEveryLineChanged()
    {
        var result = LineDiff.Compare("a\r\nb\r\nc\r\n", "a\nb\nc\n");

        result.Added.Should().Be(0);
        result.Removed.Should().Be(0);
        result.Lines.Should().HaveCount(3);
        result.Lines.Should().OnlyContain(line => line.Kind == DiffLineKind.Unchanged);
    }

    [Fact]
    public void LoneCr_IsTreatedAsALineEnding()
    {
        var result = LineDiff.Compare("a\rb", "a\nb");

        result.Added.Should().Be(0);
        result.Removed.Should().Be(0);
    }

    [Fact]
    public void TrailingNewlineDifference_IsIgnored()
    {
        var result = LineDiff.Compare("a\nb", "a\nb\n");

        result.Lines.Should().OnlyContain(line => line.Kind == DiffLineKind.Unchanged);
        result.Lines.Should().HaveCount(2);
    }

    [Fact]
    public void FileAboveMaxLines_ReturnsTooLargeQuickly_WithLineCounts()
    {
        var large = string.Join("\n", Enumerable.Range(1, LineDiff.MaxLines + 1));
        var small = "one line";

        var result = LineDiff.Compare(large, small);

        result.TooLarge.Should().BeTrue();
        result.Lines.Should().BeEmpty();
        result.Removed.Should().Be(LineDiff.MaxLines + 1);
        result.Added.Should().Be(1);
    }

    [Fact]
    public void FileAtMaxLines_IsStillDiffed()
    {
        var large = string.Join("\n", Enumerable.Range(1, LineDiff.MaxLines));

        var result = LineDiff.Compare(large, large);

        result.TooLarge.Should().BeFalse();
        result.Lines.Should().HaveCount(LineDiff.MaxLines);
    }
}
