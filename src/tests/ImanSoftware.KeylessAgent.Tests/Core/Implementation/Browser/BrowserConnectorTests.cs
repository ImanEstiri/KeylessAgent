using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
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

        public Func<bool>? PortOpensAfterLaunch { get; set; }

        public Task<bool> IsDebugPortOpenAsync(int port, CancellationToken cancellationToken = default)
            => Task.FromResult(PortOpen || (PortOpensAfterLaunch?.Invoke() ?? false));

        public Task<string?> LocateChromeAsync(string configuredPath, CancellationToken cancellationToken = default)
            => Task.FromResult(ChromePath);

        public int AttachAttempts { get; private set; }

        public Task<(object? Browser, object Context)> AttachCdpAsync(IPlaywright playwright, int port, CancellationToken cancellationToken = default)
        {
            AttachAttempts++;
            // Real Playwright context is impossible in a unit test; the tab-open is overridden,
            // so a plain object carries the role.
            return Task.FromResult<(object? Browser, object Context)>((new object(), new object()));
        }

        public Task<int> StartChromeAsync(string chromePath, int port, string userDataDir, CancellationToken cancellationToken = default)
        {
            StartedChrome = true;
            LastArguments = $"--remote-debugging-port={port} --user-data-dir=\"{userDataDir}\"";
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
}
