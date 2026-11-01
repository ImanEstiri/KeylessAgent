using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.Core;
using System.Text.Json;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// Round-trip accounting for the fallback waits, against a stubbed Playwright page: an ABSENT
/// fallback list costs exactly ONE wait with the single-wait budget (never N x timeout), and
/// a hidden earlier match never masks a later visible one (the winner probes).
/// </summary>
public class PlaywrightChatPageRoundTripTests
{
    private static readonly SelectorsCatalog Catalog =
        SelectorsCatalog.LoadAsync(Path.GetTempPath(), new Abstractions.Models.Configuration.BrowserOptions().SelectorsFile,
            CancellationToken.None).GetAwaiter().GetResult().Data!;

    private readonly IPage _page = Substitute.For<IPage>();
    private readonly ILocator _locator = Substitute.For<ILocator>();

    public PlaywrightChatPageRoundTripTests()
    {
        // All selectors resolve to the same stub by default; the Or-chain and filters collapse
        // onto it too, so one WaitForAsync call represents the whole combined race.
        _page.Locator(Arg.Any<string>()).Returns(_locator);
        _locator.Locator(Arg.Any<string>()).Returns(_locator); // scoped probes (newest message)
        _locator.Or(Arg.Any<ILocator>()).Returns(_locator);
        _locator.Filter(Arg.Any<LocatorFilterOptions>()).Returns(_locator);
        _locator.First.Returns(_locator);
        _locator.Nth(Arg.Any<int>()).Returns(_locator);
        _page.Keyboard.Returns(Substitute.For<IKeyboard>());
    }

    private PlaywrightChatPage CreatePage() => new(
        _page, Catalog, debug: false,
        new LoggerAdapter<PlaywrightChatPage>(NullLoggerFactory.Instance));

    private static int WaitCount(ILocator locator)
        => locator.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "WaitForAsync");

    private static float? WaitTimeout(ILocator locator)
        => locator.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "WaitForAsync")
            .Select(c => (LocatorWaitForOptions)c.GetArguments()[0]!)
            .Select(o => o.Timeout)
            .FirstOrDefault();

    [Fact]
    public async Task AbsentInputBox_CostsOneWait_WithTheSingleBudget()
    {
        _locator.WaitForAsync(Arg.Any<LocatorWaitForOptions>())
            .Returns(ci => throw new TimeoutException());

        var act = () => CreatePage().InsertTextAsync("hello", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        WaitCount(_locator).Should().Be(1, "one combined wait, never one per fallback selector");
        WaitTimeout(_locator).Should().Be(10_000, "the single-wait budget of the input wait");
    }

    [Fact]
    public async Task AbsentSendButton_CostsOneWait_WithTheSingleBudget()
    {
        _locator.WaitForAsync(Arg.Any<LocatorWaitForOptions>())
            .Returns(ci => throw new TimeoutException());

        var sent = await CreatePage().TryClickSendAsync(CancellationToken.None);

        sent.Should().BeFalse();
        WaitCount(_locator).Should().Be(1, "one combined wait, never one per fallback selector");
        WaitTimeout(_locator).Should().Be(3_000, "the single-wait budget of the send-button wait");
    }

    [Fact]
    public async Task AbsentMessageCopyButton_CostsOneWait_WithTheCallerBudget()
    {
        _locator.CountAsync().Returns(1); // the newest assistant message exists
        _locator.WaitForAsync(Arg.Any<LocatorWaitForOptions>())
            .Returns(ci => throw new TimeoutException());
        var page = CreatePage();

        var appeared = await page.WaitForMessageCopyButtonAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        appeared.Should().BeFalse();
        WaitCount(_locator).Should().Be(1, "one scoped combined wait, never one per fallback selector");
        WaitTimeout(_locator).Should().Be(2_000, "the caller's timeout is the budget once");
    }

    [Fact]
    public async Task HiddenEarlierMatch_DoesNotHideALaterVisibleOne()
    {
        // Distinct locators per selector: the first two are hidden, the third is visible.
        var hidden0 = Substitute.For<ILocator>();
        var hidden1 = Substitute.For<ILocator>();
        var visible2 = Substitute.For<ILocator>();
        _page.Locator(Arg.Is<string>(s => s == Catalog.InputBox[0])).Returns(hidden0);
        _page.Locator(Arg.Is<string>(s => s == Catalog.InputBox[1])).Returns(hidden1);
        _page.Locator(Arg.Is<string>(s => s == Catalog.InputBox[2])).Returns(visible2);

        // The combined Or-locator collapses onto `visible2` only for the probes; the wait
        // itself succeeds (the race fired because SOME fallback is visible).
        _locator.WaitForAsync(Arg.Any<LocatorWaitForOptions>()).Returns(Task.CompletedTask);
        hidden0.Or(Arg.Any<ILocator>()).Returns(_locator);
        hidden0.First.Returns(hidden0);
        hidden0.IsVisibleAsync().Returns(false);
        hidden1.First.Returns(hidden1);
        hidden1.IsVisibleAsync().Returns(false);
        visible2.First.Returns(visible2);
        visible2.IsVisibleAsync().Returns(true);

        var page = CreatePage();
        await page.InsertTextAsync("hello", CancellationToken.None);

        // The winner probes ran in fallback order until the first VISIBLE selector — the
        // hidden first match did not mask the later visible one, and the text went THERE.
        hidden0.Received(1).IsVisibleAsync();
        hidden1.Received(1).IsVisibleAsync();
        visible2.Received(1).IsVisibleAsync();
        visible2.Received(1).FillAsync(Arg.Any<string?>(), Arg.Any<LocatorFillOptions?>());
    }

    [Fact]
    public async Task IsAnyVisibleAbsent_CostsOneRoundTrip()
    {
        _locator.IsVisibleAsync().Returns(false);
        var page = CreatePage();

        var visible = await page.IsStopIndicatorVisibleAsync(CancellationToken.None);

        visible.Should().BeFalse();
        _locator.Received(1).IsVisibleAsync();
    }
    [Fact]
    public async Task CopyClick_AbsentFirstFallback_ClicksTheLaterVisibleOne_AfterOneWait()
    {
        // The copy-capture click loop: ONE combined Or-wait over all MessageCopyButton
        // fallbacks (the 3s-per-fallback total budget spent once), then a click on the
        // MATCHED element — never a 3s click timeout per absent fallback.
        var page = Substitute.For<IPage>();
        var combined = Substitute.For<ILocator>();
        var newest = Substitute.For<ILocator>();
        var messages = Substitute.For<ILocator>();
        var body = Substitute.For<ILocator>();
        var absent0 = Substitute.For<ILocator>();
        var absent1 = Substitute.For<ILocator>();
        var visible2 = Substitute.For<ILocator>();

        // The newest assistant message exists.
        page.Locator(Arg.Any<string>()).Returns(messages);
        messages.CountAsync().Returns(1);
        messages.Nth(Arg.Any<int>()).Returns(newest);

        // The three MessageCopyButton fallbacks resolve from the newest-message scope:
        // the first two are ABSENT (a click there can never land), the third is visible.
        var copySelectors = Catalog.MessageCopyButton;
        newest.Locator(Arg.Any<string>()).Returns(absent0); // catch-all first: NSubstitute is last-config-wins
        newest.Locator(Arg.Is<string>(s => s == copySelectors[0])).Returns(absent0);
        newest.Locator(Arg.Is<string>(s => s == copySelectors[1])).Returns(absent1);
        newest.Locator(Arg.Is<string>(s => s == copySelectors[2])).Returns(visible2);

        // The combined Or-chain races ONCE (succeeds: some fallback is visible).
        absent0.Or(Arg.Any<ILocator>()).Returns(combined);
        absent1.Or(Arg.Any<ILocator>()).Returns(combined);
        visible2.Or(Arg.Any<ILocator>()).Returns(combined);
        combined.Or(Arg.Any<ILocator>()).Returns(combined);
        combined.Filter(Arg.Any<LocatorFilterOptions>()).Returns(combined);
        combined.First.Returns(combined);

        // Winner probes in fallback order: absent, absent, visible.
        absent0.First.Returns(absent0);
        absent0.IsVisibleAsync().Returns(false);
        absent1.First.Returns(absent1);
        absent1.IsVisibleAsync().Returns(false);
        visible2.First.Returns(visible2);
        visible2.IsVisibleAsync().Returns(true);

        // The clipboard read (strategy 2) returns the copied text; the interception poll
        // (strategy 1) finds nothing, so strategy 2 with its click actually runs.
        page.Locator(Arg.Is<string>(s => s == "body")).Returns(body);
        body.First.Returns(body);
        // ILocator.EvaluateAsync (3-arg overload) returns Task<JsonElement?>: the interception
        // poll (window.__keylessCapturedText) finds nothing — forcing strategy 2 — while the
        // clipboard readText probes return the copied text.
        body.EvaluateAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<LocatorEvaluateOptions?>())
            .Returns(ci => ci.ArgAt<string>(0).Contains("__keylessCapturedText")
                ? (JsonElement?)null
                : System.Text.Json.JsonSerializer.SerializeToElement("copied text"));

        var chatPage = new PlaywrightChatPage(
            page, Catalog, debug: false,
            new LoggerAdapter<PlaywrightChatPage>(NullLoggerFactory.Instance));

        var text = await chatPage.TryCaptureCopiedReplyAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);

        // BOTH capture strategies click: each does ONE combined wait carrying the whole
        // 3s-per-fallback total budget (never one 3s click timeout per absent fallback).
        combined.Received(2).WaitForAsync(Arg.Is<LocatorWaitForOptions>(o => o.Timeout == 3_000 * copySelectors.Length));
        // The click lands on the matched element only — no 3s click timeouts on absent fallbacks.
        absent0.DidNotReceive().ClickAsync(Arg.Any<LocatorClickOptions>());
        absent1.DidNotReceive().ClickAsync(Arg.Any<LocatorClickOptions>());
        visible2.Received(2).ClickAsync(Arg.Any<LocatorClickOptions>());
    }
}
