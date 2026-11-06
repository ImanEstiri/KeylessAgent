using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Infrastructure;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;
using ImanSoftware.Outcomes;
using Xunit;
namespace ImanSoftware.KeylessAgent.Wpf.Tests.ViewModels;

public sealed class ChromeViewModelTests
{
    private readonly ActivityFeed _feed = new();
    private readonly ImmediateDispatcher _dispatcher = new();
    private readonly AgentViewModel _agent;
    private readonly FakeBrowserConnector _connector = new();
    private readonly FakeConfigurationService _configuration = new();

    public ChromeViewModelTests()
    {
        _agent = new AgentViewModel(
            new AgentRunner(
                _dispatcher,
                _feed,
                () => throw new NotSupportedException("turns are not part of these tests"),
                (_, _) => throw new NotSupportedException("turns are not part of these tests")),
            _dispatcher,
            _feed);
    }

    private readonly FakeChromeWindowController _windowController = new();

    private ChromeViewModel CreateViewModel(Func<IAgentUserInterface>? uiFactory = null)
        => new(
            _connector,
            _configuration,
            _windowController,
            _agent,
            uiFactory ?? (() => null!),
            _dispatcher);

    [Fact]
    public async Task SuccessfulStart_ShowsTheConnectionKindAndProfile()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        var viewModel = CreateViewModel();

        viewModel.StartCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsConnecting);

        _connector.CallCount.Should().Be(1);
        viewModel.ConnectionKind.Should().Be("AutoLaunchedChrome");
        viewModel.Profile.Should().Be(@"C:\profile");
        viewModel.StatusText.Should().Be("Chrome ready (AutoLaunchedChrome)");
        viewModel.Note.Should().Contain("Log in to DeepSeek");
    }

    [Fact]
    public async Task FailedConnection_ShowsTheActionableMessage_AndResetsTheStatus()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        var viewModel = CreateViewModel();
        _connector.Fail = true;

        viewModel.StartCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsConnecting);

        viewModel.Note.Should().Contain("KA_BROWSER_CONNECTION_FAILED");
        viewModel.StatusText.Should().Be("Ready");
        viewModel.ConnectionKind.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfigurationFailure_NeverTouchesTheConnector()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        var configuration = new FakeConfigurationService(fail: true);
        var viewModel = new ChromeViewModel(_connector, configuration, new FakeChromeWindowController(), _agent, () => throw new NotSupportedException(), _dispatcher);

        viewModel.StartCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.Note.Length > 0);

        viewModel.Note.Should().Contain("KA_CONFIG_INVALID");
        _connector.CallCount.Should().Be(0);
    }

    [Fact]
    public void Start_DisabledWithoutWorkspace_AndConnectorNotCalled()
    {
        var viewModel = CreateViewModel();

        viewModel.StartCommand.Execute(null);

        _connector.CallCount.Should().Be(0);
        viewModel.StatusText.Should().Be("Ready");
    }

    [Fact]
    public void Start_DisabledWhileConnecting()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        var gate = new TaskCompletionSource();
        var connector = new GateConnector(gate);
        var viewModel = new ChromeViewModel(connector, _configuration, new FakeChromeWindowController(), _agent, () => null!, _dispatcher);

        viewModel.StartCommand.Execute(null);
        viewModel.StartCommand.CanExecute(null).Should().BeFalse();

        gate.SetResult();
        WaitUntil(() => !viewModel.IsConnecting);
    }

    private sealed class GateConnector(TaskCompletionSource gate) : IBrowserConnector
    {
        public async Task<Outcome<(BrowserAttachment Attachment, object Page)>> ConnectAsync(
            BrowserOptions options, IAgentUserInterface ui, CancellationToken cancellationToken = default, ITimingRecorder? timings = null, string? resumeChatUrl = null)
        {
            await gate.Task.WaitAsync(cancellationToken);
            return Outcome.Successful(
                (new BrowserAttachment { Kind = BrowserConnectionKind.AttachedToRunningChrome, UserDataDir = @"C:\profile" }, new object()));
        }
    }


    [Fact]
    public async Task ChromeActive_Matrix_ShowHideCloseEnabled_StartDisabled()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        _windowController.State = new ChromeProfileState(DebugPortOpen: true, OwnedByAgent: true, ProcessCount: 2);
        var viewModel = CreateViewModel();

        await viewModel.RefreshChromeStateAsync();

        viewModel.IsChromeActive.Should().BeTrue();
        viewModel.StartCommand.CanExecute(null).Should().BeFalse("Chrome is already running");
        viewModel.ShowChromeCommand.CanExecute(null).Should().BeTrue();
        viewModel.HideChromeCommand.CanExecute(null).Should().BeTrue();
        viewModel.CloseChromeCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ChromeNotRunning_Matrix_OnlyStartEnabled()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        _windowController.State = new ChromeProfileState(DebugPortOpen: false, OwnedByAgent: false, ProcessCount: 0);
        var viewModel = CreateViewModel();

        await viewModel.RefreshChromeStateAsync();

        viewModel.IsChromeActive.Should().BeFalse();
        viewModel.StartCommand.CanExecute(null).Should().BeTrue();
        viewModel.ShowChromeCommand.CanExecute(null).Should().BeFalse();
        viewModel.HideChromeCommand.CanExecute(null).Should().BeFalse();
        viewModel.CloseChromeCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ForeignChrome_Matrix_EverythingDisabled_WithExplanation()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        _windowController.State = new ChromeProfileState(DebugPortOpen: true, OwnedByAgent: false, ProcessCount: 0);
        var viewModel = CreateViewModel();

        await viewModel.RefreshChromeStateAsync();

        viewModel.IsForeignChrome.Should().BeTrue("the port is held by a Chrome without our profile");
        viewModel.StartCommand.CanExecute(null).Should().BeFalse("the port is occupied by a foreign Chrome");
        viewModel.ShowChromeCommand.CanExecute(null).Should().BeFalse();
        viewModel.HideChromeCommand.CanExecute(null).Should().BeFalse();
        viewModel.CloseChromeCommand.CanExecute(null).Should().BeFalse("we never kill the user's own Chrome");
    }

    private void MakeChromeActive()
    {
        _windowController.State = new ChromeProfileState(DebugPortOpen: true, OwnedByAgent: true, ProcessCount: 1);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++)
            Thread.Sleep(10);

        condition().Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition())
                return;

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met within 5 seconds.");
    }

    [Fact]
    public async Task ShowChrome_CallsTheController_AndReportsVisibility()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        var viewModel = CreateViewModel();

        MakeChromeActive();
        await viewModel.RefreshChromeStateAsync();
        viewModel.ShowChromeCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsShowing);

        _windowController.ShowCalls.Should().Be(1);
        _windowController.LastPeekSeconds.Should().Be(0, "the button keeps the window visible");
        viewModel.StatusText.Should().Be("Chrome visible");
    }

    [Fact]
    public void ShowChrome_IsDisabled_WithoutAWorkspace()
    {
        var viewModel = CreateViewModel();
        _agent.WorkspaceRoot = string.Empty;

        viewModel.ShowChromeCommand.CanExecute(null).Should().BeFalse("no workspace selected");
    }

    [Fact]
    public async Task ShowChrome_Failure_SurfacesTheErrorInTheNote()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        _windowController.Fail = true;
        var viewModel = CreateViewModel();

        MakeChromeActive();
        await viewModel.RefreshChromeStateAsync();
        viewModel.ShowChromeCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsShowing);

        viewModel.StatusText.Should().Be("Ready");
        viewModel.Note.Should().Contain("show failed");
    }

    [Fact]
    public async Task HideChrome_CallsTheController_AndReportsOffscreen()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        var viewModel = CreateViewModel();

        MakeChromeActive();
        await viewModel.RefreshChromeStateAsync();
        viewModel.HideChromeCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsHiding);

        _windowController.HideCalls.Should().Be(1);
        viewModel.StatusText.Should().Be("Chrome offscreen");
        viewModel.Note.Should().Contain("moved offscreen");
    }

    [Fact]
    public async Task HideChrome_Failure_SurfacesTheErrorInTheNote()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        _windowController.Fail = true;
        var viewModel = CreateViewModel();

        MakeChromeActive();
        await viewModel.RefreshChromeStateAsync();
        viewModel.HideChromeCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsHiding);

        viewModel.StatusText.Should().Be("Ready");
        viewModel.Note.Should().Contain("hide failed");
    }

    [Fact]
    public void HideChrome_IsDisabled_WithoutAWorkspace()
    {
        var viewModel = CreateViewModel();
        _agent.WorkspaceRoot = string.Empty;

        viewModel.HideChromeCommand.CanExecute(null).Should().BeFalse("no workspace selected");
    }

    [Fact]
    public async Task CloseChrome_CallsTheController_AndClearsTheStaleStatus()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        var viewModel = CreateViewModel();

        MakeChromeActive();
        await viewModel.RefreshChromeStateAsync();
        viewModel.CloseChromeCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsClosing);

        _windowController.CloseCalls.Should().Be(1);
        // The close may race the ~2s state poll on the exact status wording; the INVARIANT
        // is that no Chrome-active state survives and the user is told what happened.
        viewModel.IsChromeActive.Should().BeFalse("no stale Chrome-visible state survives");
        viewModel.Note.Should().Contain("Chrome closed").And.Contain("Start Chrome");
    }

    [Fact]
    public async Task CloseChrome_Failure_SurfacesTheErrorInTheNote()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        _windowController.Fail = true;
        var viewModel = CreateViewModel();

        MakeChromeActive();
        await viewModel.RefreshChromeStateAsync();
        viewModel.CloseChromeCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsClosing);

        viewModel.StatusText.Should().Be("Ready");
        viewModel.Note.Should().Contain("close failed");
    }

    [Fact]
    public void CloseChrome_IsDisabled_WithoutAWorkspace()
    {
        var viewModel = CreateViewModel();
        _agent.WorkspaceRoot = string.Empty;

        viewModel.CloseChromeCommand.CanExecute(null).Should().BeFalse("no workspace selected");
    }

    [Fact]
    public void ShowAndHide_CrossDisable_WhileAnotherWindowActionRuns()
    {
        _agent.WorkspaceRoot = @"G:\demo";
        _windowController.SlowShow = true; // the show call blocks until released
        MakeChromeActive();
        var viewModel = CreateViewModel();
        viewModel.RefreshChromeStateAsync().Wait();

        viewModel.ShowChromeCommand.Execute(null);
        WaitUntil(() => viewModel.IsShowing);

        viewModel.HideChromeCommand.CanExecute(null).Should().BeFalse("a show action is running");
        viewModel.StartCommand.CanExecute(null).Should().BeFalse("a show action is running");

        _windowController.ReleaseShow();
        WaitUntil(() => !viewModel.IsShowing);
        viewModel.HideChromeCommand.CanExecute(null).Should().BeTrue("the show action finished");
    }
}
