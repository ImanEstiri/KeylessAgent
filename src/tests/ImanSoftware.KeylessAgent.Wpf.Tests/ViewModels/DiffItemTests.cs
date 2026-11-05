using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;
using Xunit;
using DiffLineKind = ImanSoftware.KeylessAgent.Wpf.Services.DiffLineKind;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.ViewModels;

public sealed class DiffItemTests
{
    [Fact]
    public void NewFile_IsExpandedByDefault_AndAllAdded()
    {
        var item = new DiffItem(new FileChange("src/new.cs", FileChangeKind.Create, OldContent: null, NewContent: "a\nb"));

        item.IsExpanded.Should().BeTrue();
        item.Kind.Should().Be("Create");
        item.Result.Added.Should().Be(2);
        item.Result.Removed.Should().Be(0);
        item.Rows.OfType<DiffLine>().Should().OnlyContain(line => line.Kind == DiffLineKind.Added);
    }

    [Fact]
    public void DeletedFile_IsAllRemoved()
    {
        var item = new DiffItem(new FileChange("src/old.cs", FileChangeKind.Delete, OldContent: "a", NewContent: null));

        item.Kind.Should().Be("Delete");
        item.Result.Removed.Should().Be(1);
        item.TooLarge.Should().BeFalse();
    }

    [Fact]
    public void Collapse_HidesTheBody()
    {
        var item = new DiffItem(new FileChange("a.cs", FileChangeKind.Update, "x", "y"));

        item.Collapse();

        item.IsExpanded.Should().BeFalse();
        item.ToggleCommand.Execute(null);
        item.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void LongUnchangedRun_CollapsesToAMarkerWithContextLines()
    {
        var unchanged = Enumerable.Range(1, 20).Select(i => $"line {i}").ToArray();
        var oldText = string.Join("\n", ["head", .. unchanged, "tail"]);
        var newText = string.Join("\n", ["HEAD", .. unchanged, "TAIL"]);

        var item = new DiffItem(new FileChange("a.txt", FileChangeKind.Update, oldText, newText));

        var collapsed = item.Rows.OfType<CollapsedRun>().ToList();
        collapsed.Should().ContainSingle();
        collapsed[0].Count.Should().Be(20 - DiffItem.CollapseContext * 2);

        item.Rows.OfType<DiffLine>().Count(line => line.Kind == DiffLineKind.Unchanged)
            .Should().Be(DiffItem.CollapseContext * 2);
    }

    [Fact]
    public void ShortUnchangedRun_IsNotCollapsed()
    {
        var item = new DiffItem(new FileChange("a.txt", FileChangeKind.Update, "a\nb", "a\nb\nc"));

        item.Rows.OfType<CollapsedRun>().Should().BeEmpty();
        item.Rows.OfType<DiffLine>().Should().HaveCount(3);
    }

    [Fact]
    public void TooLargeFile_ShowsTheMessage_AndIsCollapsed()
    {
        var large = string.Join("\n", Enumerable.Range(1, LineDiff.MaxLines + 1));
        var item = new DiffItem(new FileChange("big.txt", FileChangeKind.Update, large, large + "\nmore"));

        item.TooLarge.Should().BeTrue();
        item.IsExpanded.Should().BeFalse();
        item.Rows.Should().BeEmpty();
        item.TooLargeMessage.Should().Contain($"old: {LineDiff.MaxLines + 1}").And.Contain("new: 3002");
    }
}
