using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Chat;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using Microsoft.Playwright;
using NSubstitute;
using ImanSoftware.KeylessAgent.Tests.Fakes;
using ImanSoftware.Outcomes;

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

    /// <summary>Records the login flow's window-show calls (pure CDP window moves in prod).</summary>
    private sealed class RecordingWindowController : IChromeWindowController
    {
        public int ShowCalls;
        public int LastPeekSeconds = -1;

        public Task<Outcome> ShowAsync(BrowserOptions options, int peekSeconds, CancellationToken ct = default)
        {
            ShowCalls++;
            LastPeekSeconds = peekSeconds;
            return Task.FromResult(Outcome.Successful());
        }

        public Task<Outcome> HideAsync(BrowserOptions options, CancellationToken ct = default)
            => Task.FromResult(Outcome.Successful());

        public Task<Outcome> CloseAsync(BrowserOptions options, bool allowAttached = false, CancellationToken ct = default)
            => Task.FromResult(Outcome.Successful());
    }

    private async Task<RecordingWindowController> RunLoginProbeAsync(FakeChatPage page)
    {
        var provider = new PlaywrightChatProvider();
        var windowController = new RecordingWindowController();
        provider.WindowControllerFactory = _ => windowController;
        await provider.WaitForLoginIfNeeded(
            page, _catalog,
            new ChatSessionOptions { WorkspaceRoot = Path.GetTempPath(), Browser = new Abstractions.Models.Configuration.BrowserOptions() },
            CancellationToken.None);
        return windowController;
    }

    private int InputSelectorCount => _catalog.InputBox.Length;

    [Fact]
    public async Task LoggedIn_WinsTheRace_NoLoginFlowRuns()
    {
        var page = new FakeChatPage();
        page.AnyVisibleWaitResults.Enqueue(0); // the FIRST selector group (InputBox) matched.

        var windowController = await RunLoginProbeAsync(page);

        page.WaitForAnyVisibleCalls.Should().Be(1, "one parallel wait, not one per fallback selector");
        windowController.ShowCalls.Should().Be(0, "a logged-in start never shows the window");
        page.FocusedTabs.Should().BeEmpty("a logged-in start never focuses the tab");
        page.IsAnyVisibleCalls.Should().Be(0, "the login-wait loop must not run");
    }

    [Fact]
    public async Task LoginIndicatorWins_WindowIsShown_AndTheLoginWaitPollsInstantChecks()
    {
        var page = new FakeChatPage();
        page.AnyVisibleWaitResults.Enqueue(InputSelectorCount); // a LOGIN indicator matched.
        page.AnyVisibleResults.Enqueue(false); // then the indicator is gone: login done.

        var windowController = await RunLoginProbeAsync(page);

        page.WaitForAnyVisibleCalls.Should().Be(1, "the probe itself is one parallel wait");
        windowController.ShowCalls.Should().Be(1, "a hidden window would make the login page invisible");
        windowController.LastPeekSeconds.Should().Be(0, "the window stays up until the login completes");
        page.FocusedTabs.Should().BeEmpty("the window is SHOWN by window bounds — never BringToFront-focused");
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
    public async Task Dispose_ClosesTheContext_WhenWeLaunchedChromeAndCloseIsRequested()
    {
        var page = Substitute.For<IPage>();
        var context = Substitute.For<IBrowserContext>();
        page.Context.Returns(context);
        context.Pages.Returns(new List<IPage> { page });

        await PlaywrightChatProvider.DisposeAsync(
            page, new Abstractions.Models.Configuration.BrowserOptions { CloseChromeOnExit = true },
            BrowserConnectionKind.AutoLaunchedChrome);

        context.Received(1).CloseAsync();
    }

    [Fact]
    public async Task Dispose_NeverClosesAnAttachedChrome_EvenWithCloseChromeOnExit()
    {
        var page = Substitute.For<IPage>();
        var context = Substitute.For<IBrowserContext>();
        page.Context.Returns(context);
        context.Pages.Returns(new List<IPage> { page });

        await PlaywrightChatProvider.DisposeAsync(
            page, new Abstractions.Models.Configuration.BrowserOptions { CloseChromeOnExit = true },
            BrowserConnectionKind.AttachedToRunningChrome);

        context.DidNotReceive().CloseAsync();
    }

    [Fact]
    public async Task Dispose_KeepsTheContext_ByDefault()
    {
        var page = Substitute.For<IPage>();
        var context = Substitute.For<IBrowserContext>();
        page.Context.Returns(context);
        context.Pages.Returns(new List<IPage> { page });

        await PlaywrightChatProvider.DisposeAsync(
            page, new Abstractions.Models.Configuration.BrowserOptions { CloseChromeOnExit = false },
            BrowserConnectionKind.AutoLaunchedChrome);

        context.DidNotReceive().CloseAsync();
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

        var windowController = await RunLoginProbeAsync(page);

        page.WaitForAnyVisibleCalls.Should().Be(1);
        windowController.ShowCalls.Should().Be(1, "the visible LOGIN indicator wins the race");
        page.FocusedTabs.Should().BeEmpty("focus comes from window bounds now, never BringToFront");
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
