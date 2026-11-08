using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using Xunit;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// The tracker the App.OnExit Chrome-close safety net reads: the options of the LAST
/// successful connect (port + resolved profile dir) — so shutdown ownership matching uses
/// what was actually used instead of defaults that only happen to match.
/// </summary>
public class ChromeLaunchTrackerTests
{
    [Fact]
    public void Remember_StoresOptionsAndResolvedProfileDir()
    {
        ChromeLaunchTracker.Reset();

        var options = new BrowserOptions { ChromeDebugPort = 9333 };
        ChromeLaunchTracker.Remember(options, @"C:\profiles\ka");

        ChromeLaunchTracker.Last.Should().BeSameAs(options);
        ChromeLaunchTracker.LastUserDataDir.Should().Be(@"C:\profiles\ka");
    }

    [Fact]
    public void Remember_LatestWins_AndResetClears()
    {
        ChromeLaunchTracker.Reset();
        ChromeLaunchTracker.Remember(new BrowserOptions { ChromeDebugPort = 9222 }, @"C:\first");
        ChromeLaunchTracker.Remember(new BrowserOptions { ChromeDebugPort = 9333 }, @"C:\second");

        ChromeLaunchTracker.Last.ChromeDebugPort.Should().Be(9333, "the most recent connect wins");
        ChromeLaunchTracker.LastUserDataDir.Should().Be(@"C:\second");

        ChromeLaunchTracker.Reset();
        ChromeLaunchTracker.Last.Should().BeNull();
        ChromeLaunchTracker.LastUserDataDir.Should().BeEmpty();
    }
}
