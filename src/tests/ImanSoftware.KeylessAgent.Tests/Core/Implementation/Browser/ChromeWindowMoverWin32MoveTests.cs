
















using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// The Win32 move-and-keep-visible fallback for the HIDE direction: the window is restored
/// (if iconic) and moved to the tiny corner, and it must stay VISIBLE - ShowWindow(SW_HIDE)
/// is never an acceptable hide fallback because it makes Chrome occlusion-throttle.
/// </summary>
public class ChromeWindowMoverWin32MoveTests
{
    private static readonly (int Left, int Top) Position = (1840, 1000);

    [Fact]
    public void MoveHandles_VisibleAndOnPosition_ReturnsTrue()
    {
        var moves = new List<(IntPtr Hwnd, int Left, int Top)>();

        var verified = ChromeWindowMover.MoveHandles(
            [new IntPtr(1)], Position,
            isIconic: _ => false,
            restore: _ => { },
            move: (hwnd, left, top) => moves.Add((hwnd, left, top)),
            verify: _ => true);

        verified.Should().BeTrue();
        moves.Should().ContainSingle().Which.Should().Be((new IntPtr(1), 1840, 1000));
    }

    [Fact]
    public void MoveHandles_PositionDriftsOrInvisible_ReturnsFalse()
    {
        var verified = ChromeWindowMover.MoveHandles(
            [new IntPtr(1)], Position,
            isIconic: _ => false,
            restore: _ => { },
            move: (_, _, _) => { },
            verify: _ => false);

        verified.Should().BeFalse();
    }

    [Fact]
    public void MoveHandles_MinimizedWindow_IsRestoredBeforeTheMove()
    {
        var events = new List<string>();

        var verified = ChromeWindowMover.MoveHandles(
            [new IntPtr(7)], Position,
            isIconic: _ => true,
            restore: _ => events.Add("restore"),
            move: (_, _, _) => events.Add("move"),
            verify: _ => true);

        verified.Should().BeTrue();
        events.Should().Equal("restore", "move");
    }

    [Fact]
    public void HidePaths_NeverReferenceSwHide()
    {
        var root = FindRepoRoot();
        var browserDir = Path.Combine(root, "src", "Core", "ImanSoftware.KeylessAgent.Core", "Implementation", "Browser");

        foreach (var file in new[] { "ChromeWindowMinimizer.cs", "ChromeWindowController.cs" })
        {
            var text = File.ReadAllText(Path.Combine(browserDir, file));
            text.Should().NotContain("SwHide",
                $"{file} must not use SW_HIDE: the hide fallback keeps the window visible");
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
