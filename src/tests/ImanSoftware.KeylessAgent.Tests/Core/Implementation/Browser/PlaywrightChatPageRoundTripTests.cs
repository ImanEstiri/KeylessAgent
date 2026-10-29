using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.Core;

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
        visible2.Received(1).FillAsync(Arg.Any<string?>());
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
}
