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

    /// <summary>Behavior switches for the CDP fake.</summary>
    private sealed class FakeCdp
    {
        public bool SetWindowBoundsThrows;
        public bool ReadBackAlwaysMismatches;
        public bool CloseClosesPort = true; // a real Browser.close takes the debug port down
        public readonly List<(string Method, object? Args)> Calls = new();

        public JsonElement Handle(string method, Dictionary<string, object>? args)
        {
            switch (method)
            {
                case "Browser.getWindowForTarget":
                    return Json(new { windowId = 42L });
                case "Target.getTargetInfo":
                    return Json(new { targetInfo = new { targetId = "T-1" } });
                case "Browser.setWindowBounds":
                    Calls.Add((method, args));
                    if (SetWindowBoundsThrows)
                        throw new InvalidOperationException("cdp refused");
                    _lastBounds = args is not null && args.TryGetValue("bounds", out var b) ? b : null;
                    return Json(new { });
                case "Browser.close":
                    Calls.Add((method, args));
                    if (CloseClosesPort && Environment is not null)
                        Environment.PortOpen = false; // a real Browser.close takes the port down
                    return Json(new { });
                case "Browser.getWindowBounds":
                    // A real Chrome echoes what it actually applied; the verification compares
                    // this against the target. A mismatch simulates a move that never took.
                    if (ReadBackAlwaysMismatches || _lastBounds is null)
                        return Json(new { bounds = new { left = 1, top = 2, width = 3, height = 4, windowState = "minimized" } });
                    return Json(new { bounds = _lastBounds });
                default:
                    Calls.Add((method, args));
                    return Json(new { });
            }
        }

        private object? _lastBounds;
    }

    private static (ChromeWindowController Controller, FakeCdp Cdp) CreateController(int pageCount = 1)
    {
        var cdp = new FakeCdp();
        var session = Substitute.For<ICDPSession>();

        // ONE handler for every CDP call (the controller calls SendAsync with the optional
        // args filled, so a one-argument setup would never match).
        session
            .SendAsync(Arg.Any<string>(), Arg.Any<Dictionary<string, object>?>())
            .Returns(ci => cdp.Handle(ci.ArgAt<string>(0), ci.ArgAt<Dictionary<string, object>?>(1)));
        session
            .SendAsync(Arg.Any<string>())
            .Returns(ci => cdp.Handle(ci.ArgAt<string>(0), null));

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

        Environment = new FakeEnvironment { PortOpen = true };
        var controller = new ChromeWindowController(Environment)
        {
            PlaywrightFactory = _ => Task.FromResult(playwright),
        };
        return (controller, cdp);
    }

    private static FakeEnvironment? Environment { get; set; }

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
        var (controller, cdp) = CreateController();

        var outcome = await controller.ShowAsync(new BrowserOptions { ChromeDebugPort = 9222 }, 0);

        outcome.IsSuccess.Should().BeTrue("but failed: {0}", outcome.Error?.Message ?? "-");
        var bounds = cdp.Calls.Should().ContainSingle(c => c.Method == "Browser.setWindowBounds").Which.Args!
            .Should().BeOfType<Dictionary<string, object>>().Which;
        bounds["windowId"].Should().Be(42L);
        var shape = bounds["bounds"].Should().BeOfType<Dictionary<string, object>>().Which;
        shape["left"].Should().Be(100);
        shape["top"].Should().Be(100);
        shape["width"].Should().Be(1280);
        shape["height"].Should().Be(900);
        shape["windowState"].Should().Be("normal");

        var activate = cdp.Calls.Should().ContainSingle(c => c.Method == "Target.activateTarget").Which.Args!
            .Should().BeOfType<Dictionary<string, object>>().Which;
        activate["targetId"].Should().Be("T-1");
    }

    [Fact]
    public async Task Hide_MovesTheWindowToTheTinyCorner_StillNormal()
    {
        // Fully offscreen (-32000,-32000) is treated as OCCLUDED by Chrome and throttles the
        // renderer (turn-17/18): the hide geometry is a VISIBLE 80x80 bottom-right square.
        var (controller, cdp) = CreateController();
        var (screenWidth, screenHeight) = BrowserEnvironment.PrimaryScreenSize();

        var outcome = await controller.HideAsync(new BrowserOptions { ChromeDebugPort = 9222 });

        outcome.IsSuccess.Should().BeTrue();
        var shape = cdp.Calls.Should().ContainSingle(c => c.Method == "Browser.setWindowBounds").Which.Args!
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
    public async Task CloseAsync_GracefulCdpClose_ConfirmedByThePortGoingDown()
    {
        var (controller, cdp) = CreateController();
        controller.ProcessInfosOverride = () =>
            [(111, 0, $"--user-data-dir={BrowserConnector.ResolveUserDataDir(new BrowserOptions())} --remote-debugging-port=9222")];
        try
        {
            var outcome = await controller.CloseAsync(new BrowserOptions { ChromeDebugPort = 9222 }, allowAttached: false);
            outcome.IsSuccess.Should().BeTrue();
            cdp.Calls.Should().ContainSingle(c => c.Method == "Browser.close",
                "the graceful path closes the WHOLE browser, not one tab");
        }
        finally
        {
            controller.ProcessInfosOverride = null;
        }
    }

    [Fact]
    public async Task CloseAsync_PortStaysUp_FallsBackToKill_AndFailsHonest_WhenProcessesSurvive()
    {
        var (controller, cdp) = CreateController();
        cdp.CloseClosesPort = false; // the graceful close "succeeds" but the port stays up
        var killed = new List<int>();
        controller.ProcessInfosOverride = () =>
            [(111, 0, $"--user-data-dir={BrowserConnector.ResolveUserDataDir(new BrowserOptions())} --remote-debugging-port=9222")];
        controller.KillOverride = pid => killed.Add(pid);
        ChromeWindowMover.Win32ShowWindowsOverride = (_, _, _, _, _, _) => false;
        try
        {
            var outcome = await controller.CloseAsync(new BrowserOptions { ChromeDebugPort = 9222 });

            killed.Should().Contain(111, "the tree kill reached the owned pid");
            outcome.IsFailure.Should().BeTrue("the kill fallback cannot verify against a fake port that never closes");
            outcome.Error.Message.Should().Contain("could not be closed");
            cdp.Calls.Should().ContainSingle(c => c.Method == "Browser.close",
                "the graceful attempt still ran before the kill fallback");
        }
        finally
        {
            controller.ProcessInfosOverride = null;
            controller.KillOverride = null;
            ChromeWindowMover.Win32ShowWindowsOverride = null;
        }
    }

    [Fact]
    public async Task CloseAsync_AttachedOnlyChrome_IsRefused_WithoutKilling()
    {
        // A Chrome matched ONLY by our debug port (no KeylessAgent profile dir) is the
        // user's own browser - refused by default, exactly like AttachedToRunningChrome.
        var (controller, cdp) = CreateController();
        controller.ProcessInfosOverride = () => [(222, 0, "--remote-debugging-port=9222")];
        try
        {
            var outcome = await controller.CloseAsync(new BrowserOptions { ChromeDebugPort = 9222 });

            outcome.IsFailure.Should().BeTrue();
            outcome.Error.Message.Should().Contain("was NOT closed",
                "the user's own Chrome must never be silently killed");
            cdp.Calls.Should().NotContain(c => c.Method == "Browser.close",
                "nothing is sent to a refused browser");
        }
        finally
        {
            controller.ProcessInfosOverride = null;
        }
    }

    [Fact]
    public async Task CloseAsync_AttachedOnlyChrome_AllowedWithTheExplicitFlag()
    {
        var (controller, cdp) = CreateController();
        controller.ProcessInfosOverride = () => [(222, 0, "--remote-debugging-port=9222")];
        try
        {
            var outcome = await controller.CloseAsync(new BrowserOptions { ChromeDebugPort = 9222 }, allowAttached: true);

            outcome.IsSuccess.Should().BeTrue();
            cdp.Calls.Should().ContainSingle(c => c.Method == "Browser.close");
        }
        finally
        {
            controller.ProcessInfosOverride = null;
        }
    }

    [Fact]
    public async Task CloseAsync_NoChromeAtAll_FailsWithNothingToClose()
    {
        var (controller, _) = CreateController();
        controller.ProcessInfosOverride = () => [];
        Environment!.PortOpen = false;

        var outcome = await controller.CloseAsync(new BrowserOptions { ChromeDebugPort = 9222 });

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Message.Should().Contain("nothing to close");
    }

    [Fact]
    public async Task CdpFailure_FallsBackToWin32_AndFailsHonest_WhenTheFallbackCannotVerify()
    {
        var (controller, cdp) = CreateController();
        cdp.SetWindowBoundsThrows = true;
        ChromeWindowMover.Win32ShowWindowsOverride = (_, _, _, _, _, _) => false;
        try
        {
            var outcome = await controller.ShowAsync(new BrowserOptions { ChromeDebugPort = 9222 }, 0);

            outcome.IsFailure.Should().BeTrue("a move that could not be verified must not be reported as success");
            outcome.Error.Message.Should().Be("Chrome window could not be located or moved - it may have been closed.");
        }
        finally
        {
            ChromeWindowMover.Win32ShowWindowsOverride = null;
        }
    }

    [Fact]
    public async Task UnverifiedReadBack_FallsBackToWin32_AndSucceedsWhenTheFallbackVerifies()
    {
        // The bounds were commanded but Chrome never applied them (read-back always
        // mismatched): the Win32 pass saves the day and the outcome is an honest success.
        var (controller, cdp) = CreateController();
        cdp.ReadBackAlwaysMismatches = true;
        ChromeWindowMover.Win32ShowWindowsOverride = (_, _, _, _, _, _) => true;
        try
        {
            var outcome = await controller.ShowAsync(new BrowserOptions { ChromeDebugPort = 9222 }, 0);

            outcome.IsSuccess.Should().BeTrue("the Win32 fallback verified the show");
        }
        finally
        {
            ChromeWindowMover.Win32ShowWindowsOverride = null;
        }
    }

    [Fact]
    public void PickChatPage_PrefersTheChatUrl_AndFallsBackToTheFirstPage()
    {
        var other = Substitute.For<IPage>();
        other.Url.Returns("https://example.com/some-tab");
        var chat = Substitute.For<IPage>();
        chat.Url.Returns("https://chat.deepseek.com/a/chat");
        var context = Substitute.For<IBrowserContext>();

        context.Pages.Returns([other, chat]);
        ChromeWindowMover.PickChatPage(context).Should().BeSameAs(chat,
            "with several tabs open the CHAT window is the one that must be moved");

        context.Pages.Returns([other]);
        ChromeWindowMover.PickChatPage(context).Should().BeSameAs(other,
            "no chat match falls back to the first page");

        context.Pages.Returns([]);
        ChromeWindowMover.PickChatPage(context).Should().BeNull(
            "an empty context lets the caller create a page");
    }
}
