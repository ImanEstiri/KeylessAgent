using System.Text.Json;
using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using Microsoft.Playwright;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// The window controller's contract: closed port → actionable failure; open port → the exact
/// CDP window moves (show: normal @ 100,100 + activate; hide: offscreen normal) and a session
/// detach in every path. Pure window moves — nothing else ever touches the page.
/// </summary>
public class ChromeWindowControllerTests
{
    private sealed class FakeEnvironment : IBrowserEnvironment
    {
        public bool PortOpen { get; set; }

        public Task<bool> IsDebugPortOpenAsync(int port, CancellationToken cancellationToken = default)
            => Task.FromResult(PortOpen);

        public Task<string?> LocateChromeAsync(string configuredPath, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<(object? Browser, object Context)> AttachCdpAsync(IPlaywright playwright, int port, CancellationToken cancellationToken = default)
            => Task.FromResult<(object? Browser, object Context)>((null, new object()));

        public Task<int> StartChromeAsync(string chromePath, int port, string userDataDir, string windowMode, CancellationToken cancellationToken = default, Action<string>? onCommandLine = null)
            => Task.FromResult(0);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static (ChromeWindowController Controller, List<(string Method, object? Args)> Calls) CreateController(int pageCount = 1)
    {
        var calls = new List<(string Method, object? Args)>();
        var session = Substitute.For<ICDPSession>();

        // ONE handler for every CDP call (the controller calls SendAsync with the optional
        // args filled, so a one-argument setup would never match): the no-argument queries
        // answer their fixtures, everything else is recorded and answered with {}.
        session
            .SendAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>())
            .Returns(ci =>
            {
                var method = ci.ArgAt<string>(0);
                return method switch
                {
                    "Browser.getWindowForTarget" => Json(new { windowId = 42L }),
                    "Target.getTargetInfo" => Json(new { targetInfo = new { targetId = "T-1" } }),
                    _ => TrackAndAnswer(),
                };

                System.Text.Json.JsonElement TrackAndAnswer()
                {
                    calls.Add((method, ci.ArgAt<Dictionary<string, object>?>(1)));
                    return Json(new { });
                }
            });

        var page = Substitute.For<IPage>();
        page.Context.Returns(Substitute.For<IBrowserContext>());
        page.Context.NewCDPSessionAsync(page).Returns(session);

        var context = Substitute.For<IBrowserContext>();
        context.Pages.Returns([.. Enumerable.Range(0, pageCount).Select(_ => page)]);
        context.NewPageAsync().Returns(page);

        var browser = Substitute.For<IBrowser>();
        browser.Contexts.Returns([context]);

        // Chromium must be a PINNED substitute: every access to playwright.Chromium would
        // otherwise return a fresh auto-substitute and lose the ConnectOverCDPAsync setup.
        var chromium = Substitute.For<IBrowserType>();
        chromium.ConnectOverCDPAsync(Arg.Any<string>(), Arg.Any<BrowserTypeConnectOverCDPOptions>())
            .Returns(browser);
        var playwright = Substitute.For<IPlaywright>();
        playwright.Chromium.Returns(chromium);

        var controller = new ChromeWindowController(new FakeEnvironment { PortOpen = true })
        {
            PlaywrightFactory = _ => Task.FromResult(playwright),
        };
        return (controller, calls);
    }

    [Fact]
    public async Task PortClosed_FailsWithTheChromeNotRunningMessage()
    {
        var controller = new ChromeWindowController(new FakeEnvironment { PortOpen = false });

        var outcome = await controller.ShowAsync(new BrowserOptions { ChromeDebugPort = 9222 }, 0);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Message.Should().Be("Chrome is not running on port 9222. Run 'keyless chrome' first.");

        var hide = await controller.HideAsync(new BrowserOptions { ChromeDebugPort = 9222 });
        hide.Error.Message.Should().Contain("Run 'keyless chrome' first.");
    }

    [Fact]
    public async Task Show_NormalizesTheWindowAt100x100_AndActivatesTheTarget()
    {
        var (controller, calls) = CreateController();

        var outcome = await controller.ShowAsync(new BrowserOptions { ChromeDebugPort = 9222 }, 0);

        outcome.IsSuccess.Should().BeTrue("but failed: {0}", outcome.Error?.Message ?? "-");
        var bounds = calls.Should().ContainSingle(c => c.Method == "Browser.setWindowBounds").Which.Args!
            .Should().BeOfType<Dictionary<string, object>>().Which;
        bounds["windowId"].Should().Be(42L);
        var shape = bounds["bounds"].Should().BeOfType<Dictionary<string, object>>().Which;
        shape["left"].Should().Be(100);
        shape["top"].Should().Be(100);
        shape["width"].Should().Be(1280);
        shape["height"].Should().Be(900);
        shape["windowState"].Should().Be("normal");

        var activate = calls.Should().ContainSingle(c => c.Method == "Target.activateTarget").Which.Args!
            .Should().BeOfType<Dictionary<string, object>>().Which;
        activate["targetId"].Should().Be("T-1");
    }

    [Fact]
    public async Task Hide_MovesTheWindowToTheTinyCorner_StillNormal()
    {
        // Fully offscreen (-32000,-32000) is treated as OCCLUDED by Chrome and throttles the
        // renderer (turn-17/18): the hide geometry is a VISIBLE 80x80 bottom-right square.
        var (controller, calls) = CreateController();
        var (screenWidth, screenHeight) = BrowserEnvironment.PrimaryScreenSize();

        var outcome = await controller.HideAsync(new BrowserOptions { ChromeDebugPort = 9222 });

        outcome.IsSuccess.Should().BeTrue();
        var shape = calls.Should().ContainSingle(c => c.Method == "Browser.setWindowBounds").Which.Args!
            .Should().BeOfType<Dictionary<string, object>>().Which["bounds"]
            .Should().BeOfType<Dictionary<string, object>>().Which;
        shape["left"].Should().Be(screenWidth - 80);
        shape["top"].Should().Be(screenHeight - 80);
        shape["width"].Should().Be(80);
        shape["height"].Should().Be(80);
        shape["windowState"].Should().Be("normal",
            "visible on-screen so Chrome does not occlude-throttle, unlike minimized windows");
    }

    [Fact]
    public async Task NoPages_CreatesOne()
    {
        var (controller, _) = CreateController(pageCount: 0);

        var outcome = await controller.ShowAsync(new BrowserOptions { ChromeDebugPort = 9222 }, 0);

        outcome.IsSuccess.Should().BeTrue("but failed: {0}", outcome.Error?.Message ?? "-");
    }

    [Fact]
    public async Task CdpFailure_FailsInsteadOfThrowing()
    {
        var (controller, _) = CreateController();
        // Re-wire the fake so the first setWindowBounds throws: rebuild with a throwing session.
        var session = Substitute.For<ICDPSession>();
        session.SendAsync("Browser.getWindowForTarget").Returns(Json(new { windowId = 42L }));
        session.SendAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>())
            .ThrowsAsync(new InvalidOperationException("cdp refused"));
        var page = Substitute.For<IPage>();
        page.Context.Returns(Substitute.For<IBrowserContext>());
        page.Context.NewCDPSessionAsync(page).Returns(session);
        var context = Substitute.For<IBrowserContext>();
        context.Pages.Returns([page]);
        var browser = Substitute.For<IBrowser>();
        browser.Contexts.Returns([context]);
        var chromium = Substitute.For<IBrowserType>();
        chromium.ConnectOverCDPAsync(Arg.Any<string>(), Arg.Any<BrowserTypeConnectOverCDPOptions>())
            .Returns(browser);
        var playwright = Substitute.For<IPlaywright>();
        playwright.Chromium.Returns(chromium);
        controller.PlaywrightFactory = _ => Task.FromResult(playwright);

        var outcome = await controller.ShowAsync(new BrowserOptions { ChromeDebugPort = 9222 }, 0);

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Message.Should().Contain("cdp refused");
    }
}
