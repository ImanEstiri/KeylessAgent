using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Tests.Fakes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// The positive-first login probe: ONE parallel wait races the chat input against the login
/// indicator; the round-trip count of the fake page proves an absent element is never waited
/// on per fallback, and a logged-in start never touches the login flow.
/// </summary>
public class PlaywrightChatProviderTests
{
    private readonly SelectorsCatalog _catalog =
        SelectorsCatalog.LoadAsync(Path.GetTempPath(), new Abstractions.Models.Configuration.BrowserOptions().SelectorsFile, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult().Data!;

    private async Task RunLoginProbeAsync(FakeChatPage page)
    {
        var provider = new PlaywrightChatProvider();
        await provider.WaitForLoginIfNeeded(
            page, _catalog,
            new ChatSessionOptions { WorkspaceRoot = Path.GetTempPath(), Browser = new Abstractions.Models.Configuration.BrowserOptions() },
            CancellationToken.None);
    }

    private int InputSelectorCount => _catalog.InputBox.Length;

    [Fact]
    public async Task LoggedIn_WinsTheRace_NoLoginFlowRuns()
    {
        var page = new FakeChatPage();
        page.AnyVisibleWaitResults.Enqueue(0); // the FIRST selector group (InputBox) matched.

        await RunLoginProbeAsync(page);

        page.WaitForAnyVisibleCalls.Should().Be(1, "one parallel wait, not one per fallback selector");
        page.FocusedTabs.Should().BeEmpty("a logged-in start never focuses the tab");
        page.IsAnyVisibleCalls.Should().Be(0, "the login-wait loop must not run");
    }

    [Fact]
    public async Task LoginIndicatorWins_TabIsFocused_AndTheLoginWaitPollsInstantChecks()
    {
        var page = new FakeChatPage();
        page.AnyVisibleWaitResults.Enqueue(InputSelectorCount); // a LOGIN indicator matched.
        page.AnyVisibleResults.Enqueue(false); // then the indicator is gone: login done.

        await RunLoginProbeAsync(page);

        page.WaitForAnyVisibleCalls.Should().Be(1, "the probe itself is one parallel wait");
        page.FocusedTabs.Should().ContainSingle("the login page needs the user's attention");
        page.IsAnyVisibleCalls.Should().BeGreaterThanOrEqualTo(1, "the login wait polls with instant checks");
        page.LastAnyVisibleFirstSelector.Should().Be(_catalog.LoginIndicator[0],
            "the login wait only probes the login indicators, never the input");
    }

    [Fact]
    public async Task ProbeTimeout_NothingMatched_TreatedAsLoggedIn()
    {
        // null = nothing matched within the probe window; the send path will surface any real
        // problem, so the login flow must not run on an inconclusive probe.
        var page = new FakeChatPage();
        page.AnyVisibleWaitResults.Enqueue(null);

        await RunLoginProbeAsync(page);

        page.FocusedTabs.Should().BeEmpty();
        page.IsAnyVisibleCalls.Should().Be(0);
    }

    [Fact]
    public async Task HiddenEarlierMatch_DoesNotHideALaterVisibleMatch()
    {
        // A real page can render a HIDDEN element matching the first fallback while a later
        // fallback is visible: the visibility race must pick the later visible selector, not
        // stop at the hidden first-in-DOM-order match.
        var page = new FakeChatPage();
        page.DomVisibility[_catalog.InputBox[0]] = false; // hidden, but first in fallback order
        page.DomVisibility[_catalog.LoginIndicator[0]] = true;
        page.AnyVisibleResults.Enqueue(false); // then the indicator is gone: login done

        await RunLoginProbeAsync(page);

        page.WaitForAnyVisibleCalls.Should().Be(1);
        page.FocusedTabs.Should().ContainSingle("the visible LOGIN indicator wins the race");
    }

    [Fact]
    public async Task WinnerRule_PicksFirstVisibleSelectorInFallbackOrder()
    {
        // The winner rule is pure over per-selector visibility: the first VISIBLE selector in
        // fallback order wins; hidden earlier matches are skipped; nothing visible → null.
        var selectors = new[] { "a", "b", "c" };
        var visibility = new Dictionary<string, bool>
        {
            ["a"] = false, // a hidden earlier match must not win
            ["b"] = true,
            ["c"] = true,
        };

        var winner = await PlaywrightChatPage.SelectVisibleWinnerAsync(
            selectors, selector => Task.FromResult(visibility[selector]));

        winner.Should().Be(1, "b is the first VISIBLE selector in fallback order");

        var noneVisible = await PlaywrightChatPage.SelectVisibleWinnerAsync(
            selectors, _ => Task.FromResult(false));
        noneVisible.Should().BeNull();
    }
}
