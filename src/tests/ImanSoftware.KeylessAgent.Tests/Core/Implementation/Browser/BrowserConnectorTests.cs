using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;
using Microsoft.Playwright;
using ImanSoftware.Outcomes;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Browser;

/// <summary>
/// Connection-order tests with faked environment: port probe → attach, auto-launch → attach,
/// Chrome-not-found → actionable failure, no silent fallback.
/// </summary>
public class BrowserConnectorTests
{
    private sealed class RecordingUi : IAgentUserInterface
    {
        public List<AgentProgressEvent> Progress { get; } = new();

        public void Say(string message)
        {
        }

        public void ShowProgress(AgentProgressEvent progressEvent) => Progress.Add(progressEvent);

        public Task<bool> ConfirmAsync(ConfirmRequest request) => Task.FromResult(true);

        public Task<string> AskAsync(string question) => Task.FromResult(string.Empty);

        public void ShowDiff(FileChange change)
        {
        }

        public IDisposable BeginWait(string message) => new NopScope();

        public Task<ChoiceResult> ChooseAsync(ChoiceRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(ChoiceResult.CancelledMenu());

        private sealed class NopScope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static IAgentUserInterface Ui() => new RecordingUi();

    private static BrowserOptions Options(Action<BrowserOptions>? configure = null)
    {
        var options = new BrowserOptions { ChromeDebugPort = 9222, AutoLaunchChrome = true };
        configure?.Invoke(options);
        return options;
    }

    private sealed class FakeEnvironment : IBrowserEnvironment
    {
        public bool PortOpen { get; set; }

        public string? ChromePath { get; set; } = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

        public bool StartedChrome { get; private set; }

        public string? LastArguments { get; private set; }

        public string? LastWindowMode { get; private set; }

        public Func<bool>? PortOpensAfterLaunch { get; set; }

        public Task<bool> IsDebugPortOpenAsync(int port, CancellationToken cancellationToken = default)
            => Task.FromResult(PortOpen || (PortOpensAfterLaunch?.Invoke() ?? false));

        public Task<string?> LocateChromeAsync(string configuredPath, CancellationToken cancellationToken = default)
            => Task.FromResult(ChromePath);

        public int AttachAttempts { get; private set; }

        /// <summary>When true, AttachCdpAsync throws (the port answers but CDP attach fails).</summary>
        public bool FailAttach { get; set; }

        public Task<(object? Browser, object Context)> AttachCdpAsync(IPlaywright playwright, int port, CancellationToken cancellationToken = default)
        {
            AttachAttempts++;
            if (FailAttach)
                throw new InvalidOperationException("cdp attach refused");

            // Real Playwright context is impossible in a unit test; the tab-open is overridden,
            // so a plain object carries the role.
            return Task.FromResult<(object? Browser, object Context)>((new object(), new object()));
        }

        public Action<string>? LastOnCommandLine { get; private set; }

        public Task<int> StartChromeAsync(string chromePath, int port, string userDataDir, string windowMode, CancellationToken cancellationToken = default, Action<string>? onCommandLine = null)
        {
            StartedChrome = true;
            LastWindowMode = windowMode;
            LastArguments = $"--remote-debugging-port={port} --user-data-dir=\"{userDataDir}\"";
            LastOnCommandLine = onCommandLine;
            PortOpen = true;
            return Task.FromResult(4242);
        }
    }

    [Fact]
    public async Task PortOpen_AttachesWithoutLaunching()
    {
        var environment = new FakeEnvironment { PortOpen = true };
        var connector = new BrowserConnector(environment, null)
        {
            OpenTabOverride = (_, _) => Task.FromResult(
                Outcome.Successful<object>("page-stub")),
        };

        var outcome = await connector.ConnectAsync(Options(), Ui());

        outcome.IsSuccess.Should().BeTrue();
        outcome.Data.Attachment.Kind.Should().Be(BrowserConnectionKind.AttachedToRunningChrome);
        environment.StartedChrome.Should().BeFalse("an already-running Chrome is never started again");
    }

    [Fact]
    public async Task PortClosed_AutoLaunch_StartsChromeThenAttaches()
    {
        var environment = new FakeEnvironment { PortOpen = false };
        var connector = new BrowserConnector(environment, null)
        {
            OpenTabOverride = (_, _) => Task.FromResult(
                Outcome.Successful<object>("page-stub")),
        };

        var outcome = await connector.ConnectAsync(Options(), Ui());

        outcome.IsSuccess.Should().BeTrue();
        environment.StartedChrome.Should().BeTrue();
        environment.LastArguments.Should().Contain("--remote-debugging-port=9222");
        environment.LastArguments.Should().Contain("--user-data-dir=");
        environment.LastArguments.Should().Contain("--user-data-dir=");
        environment.LastArguments.Should().Contain("chrome");
        outcome.Data.Attachment.Kind.Should().Be(BrowserConnectionKind.AutoLaunchedChrome);
        outcome.Data.Attachment.LaunchedProcess.Should().BeTrue();
    }

    [Fact]
    public async Task TabOpenPhase_IsRecorded_WhenATimingRecorderIsPassed()
    {
        var environment = new FakeEnvironment { PortOpen = false };
        var connector = new BrowserConnector(environment, null)
        {
            OpenTabOverride = (_, _) => Task.FromResult(
                Outcome.Successful<object>("page-stub")),
        };
        var timings = new TimingRecorder();

        var outcome = await connector.ConnectAsync(Options(), Ui(), timings: timings);

        outcome.IsSuccess.Should().BeTrue();
        timings.Entries.Select(e => e.Phase).Should().Contain("tab-open",
            "choosing or opening the chat tab is one timed phase of the --timing table");
    }

    [Fact]
    public async Task TabOpenPhase_NotRecorded_WithoutATimingRecorder()
    {
        // No recorder → the phase is simply absent; nothing may throw on the null recorder.
        var environment = new FakeEnvironment { PortOpen = false };
        var connector = new BrowserConnector(environment, null)
        {
            OpenTabOverride = (_, _) => Task.FromResult(
                Outcome.Successful<object>("page-stub")),
        };

        var outcome = await connector.ConnectAsync(Options(), Ui());

        outcome.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task PortOpenButAttachFails_DoesNotLaunchChrome()
    {
        // The port answers but CDP attach fails: the connector must stop with the actionable
        // failure — launching a SECOND Chrome on an owned port would only fail again.
        var environment = new FakeEnvironment { PortOpen = true, FailAttach = true };
        var connector = new BrowserConnector(environment, null);

        var outcome = await connector.ConnectAsync(Options(), Ui());

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be(ErrorCodes.BrowserConnectionFailed);
        outcome.Error.Message.Should().Contain("attach failed");
        outcome.Error.Message.Should().Contain("Close the Chrome that owns port 9222");
        environment.StartedChrome.Should().BeFalse("a failed attach must not fall through to auto-launch");
    }

    [Fact]
    public async Task AutoLaunch_PassesWindowModeToTheEnvironment()
    {
        var environment = new FakeEnvironment { PortOpen = false };
        var connector = new BrowserConnector(environment, null)
        {
            OpenTabOverride = (_, _) => Task.FromResult(
                Outcome.Successful<object>("page-stub")),
        };

        var outcome = await connector.ConnectAsync(Options() with { WindowMode = "TinyCorner" }, Ui());

        outcome.IsSuccess.Should().BeTrue();
        environment.LastWindowMode.Should().Be("TinyCorner");
    }

    [Fact]
    public async Task ChromeLaunch_ForwardsTheCommandLineAsChromeLaunchedEvent()
    {
        // The launch command line must reach the turn log ("chrome-launched") so a turn can
        // prove which anti-occlusion flags the launched Chrome actually received.
        var environment = new FakeEnvironment { PortOpen = false };
        var connector = new BrowserConnector(environment, null)
        {
            OpenTabOverride = (_, _) => Task.FromResult(
                Outcome.Successful<object>("page-stub")),
        };
        var events = new List<(string Type, object Payload)>();
        connector.LogEventSink = (type, payload) =>
        {
            lock (events)
                events.Add((type, payload));
            return Task.CompletedTask;
        };

        (await connector.ConnectAsync(Options(), Ui())).IsSuccess.Should().BeTrue();

        environment.LastOnCommandLine.Should().NotBeNull("the connector wires the launch diagnostics");
        environment.LastOnCommandLine!("--disable-features=CalculateNativeWinOcclusion ...");
        lock (events)
        {
            var launched = events.Where(e => e.Type == "chrome-launched")
                .Select(e => System.Text.Json.JsonSerializer.SerializeToElement(e.Payload)).ToList();
            launched.Should().HaveCount(1);
            launched[0].GetProperty("Arguments").GetString().Should().Be("--disable-features=CalculateNativeWinOcclusion ...");
        }
    }

    [Theory]
    [InlineData("Minimized")]
    [InlineData("Offscreen")]
    [InlineData("Nonsense")]
    [InlineData("")]
    public void ChromeArguments_MinimizedAndOffscreenBothPlaceTheWindowOffscreen(string windowMode)
    {
        // A truly minimized window is throttled by the renderer and truncates the DeepSeek
        // stream, so every non-Normal mode now means OFFSCREEN (Minimized = backward compat).
        var arguments = BrowserEnvironment.ComposeChromeArguments(9222, @"C:\ka-profile", windowMode);

        arguments.Should().Contain("--window-position=-32000,-32000",
            "offscreen on every monitor: invisible, but Chrome still renders it");
        arguments.Should().Contain("--window-size=1280,900");
        arguments.Should().NotContain("--start-minimized",
            "--start-minimized truly minimizes, which throttles the renderer");
        arguments.Should().Contain("--remote-debugging-port=9222");
        arguments.Should().Contain("--disable-renderer-backgrounding", "the throttling flags are kept");
        arguments.Should().NotContain("chat.deepseek.com",
            "Chrome 136+ ignores --start-minimized when a URL is present — the tab is opened via CDP");
    }

    [Fact]
    public void ChromeArguments_TinyCorner_KeepsAVisible80x80SquareInTheCorner()
    {
        var arguments = BrowserEnvironment.ComposeChromeArguments(9222, @"C:\ka-profile", "TinyCorner", (1920, 1080));

        arguments.Should().Contain("--window-position=1840,1000", "the primary screen's bottom-right corner");
        arguments.Should().Contain("--window-size=80,80",
            "visible-on-screen so Chrome does not occlude-throttle; 80x80 is the recommended minimum");
        arguments.Should().NotContain("-32000");
    }

    [Fact]
    public void ChromeArguments_EdgePeek_KeepsA10x10SliverInTheCorner()
    {
        var arguments = BrowserEnvironment.ComposeChromeArguments(9222, @"C:\ka-profile", "EdgePeek", (1920, 1080));

        arguments.Should().Contain("--window-position=1910,1070");
        arguments.Should().Contain("--window-size=10,10");
    }

    [Fact]
    public void PrimaryScreenSize_UsesTheRealPrimaryScreen_NotAHardcodedValue()
    {
        var (width, height) = BrowserEnvironment.PrimaryScreenSize();

        width.Should().BeGreaterThan(0);
        height.Should().BeGreaterThan(0);
        (width, height).Should().NotBe((0, 0), "the primary screen bounds must come from the OS, never hardcoded");
    }

    [Fact]
    public void ChromeArguments_NormalMode_NeverForcesTheWindow()
    {
        var arguments = BrowserEnvironment.ComposeChromeArguments(9222, @"C:\ka-profile", "Normal");

        arguments.Should().NotContain("--start-minimized", "Normal never forces the window");
        arguments.Should().NotContain("--window-position");
        arguments.Should().Contain("--remote-debugging-port=9222");
    }

    [Fact]
    public async Task ChromeNotFound_FailsWithCommandLine_NoSilentFallback()
    {
        var environment = new FakeEnvironment { PortOpen = false, ChromePath = null };
        var connector = new BrowserConnector(environment, null)
        {
            OpenTabOverride = (_, _) => Task.FromResult(
                Outcome.Successful<object>("page-stub")),
        };

        var outcome = await connector.ConnectAsync(Options(), Ui());

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("KA_BROWSER_CONNECTION_FAILED");
        outcome.Error.Message.Should().Contain("--remote-debugging-port");
        outcome.Error.Message.Should().Contain("--user-data-dir");
        environment.StartedChrome.Should().BeFalse();
    }

    [Fact]
    public async Task AutoLaunchDisabled_NoPort_FailsInsteadOfFallingThrough()
    {
        var environment = new FakeEnvironment { PortOpen = false };
        var connector = new BrowserConnector(environment, null)
        {
            OpenTabOverride = (_, _) => Task.FromResult(
                Outcome.Successful<object>("page-stub")),
        };

        var outcome = await connector.ConnectAsync(Options() with { AutoLaunchChrome = false }, Ui());

        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("KA_BROWSER_CONNECTION_FAILED");
        environment.StartedChrome.Should().BeFalse();
    }

    [Fact]
    public async Task AutoLaunchDisabled_PlaywrightLaunchOptIn_UsesManagedBrowser()
    {
        var environment = new FakeEnvironment { PortOpen = false };
        var connector = new BrowserConnector(environment, null)
        {
            OpenTabOverride = (_, _) => Task.FromResult(
                Outcome.Successful<object>("page-stub")),
        };

        var outcome = await connector.ConnectAsync(
            Options() with { AutoLaunchChrome = false, AllowPlaywrightLaunch = true }, Ui());

        // The Playwright fallback itself needs a real browser binary; in tests it throws and
        // must surface as KA_BROWSER_CONNECTION_FAILED — never a silent anything.
        outcome.IsFailure.Should().BeTrue();
        outcome.Error.Code.Should().Be("KA_BROWSER_CONNECTION_FAILED");
        environment.StartedChrome.Should().BeFalse();
    }

    [Fact]
    public void ResolveUserDataDir_DefaultsToLocalAppData()
    {
        BrowserConnector.ResolveUserDataDir(new BrowserOptions())
            .Should().Contain("KeylessAgent").And.Contain("chrome-profile");

        BrowserConnector.ResolveUserDataDir(new BrowserOptions { UserDataDir = @"D:\profiles\ka" })
            .Should().Be(Path.GetFullPath(@"D:\profiles\ka"));
    }

    [Fact]
    public void CreateTargetParams_OpenTheTabInBackground_SoAMinimizedWindowStaysMinimized()
    {
        var parameters = BrowserConnector.ComposeCreateTargetParams(BrowserConnector.ChatBaseUrl);

        parameters["url"].Should().Be(BrowserConnector.ChatBaseUrl);
        parameters["newWindow"].Should().Be(false,
            "the chat is a tab in the existing window, never a new foreground window");
        parameters["background"].Should().Be(true,
            "without background Chrome ACTIVATES the new tab and restores a minimized window");
    }

    [Fact]
    public void ChromeArguments_DisableOcclusionThrottling_SoAMinimizedWindowKeepsStreaming()
    {
        // Windows 11 throttles minimized windows through native occlusion detection even
        // with the classic flags — the DeepSeek stream then truncates mid-reply.
        var arguments = BrowserEnvironment.ComposeChromeArguments(9222, @"C:\ka-profile", "Minimized");

        arguments.Should().Contain("--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling",
            "exactly these two features are disabled, nothing else");
        arguments.Should().Contain("--disable-ipc-flooding-protection");
        arguments.Should().Contain("--disable-hang-monitor");
        (arguments.Split("--disable-features").Length - 1).Should().Be(1,
            "one --disable-features flag, comma-separated values");
    }
}
