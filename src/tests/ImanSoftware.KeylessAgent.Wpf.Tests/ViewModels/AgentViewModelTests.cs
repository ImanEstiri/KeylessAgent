using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Services;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Dialogs;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.ViewModels;

public sealed class AgentViewModelTests
{
    /// <summary>
    /// Fake IAgentLoop: returns the configured result, throws the configured exception, or holds
    /// the turn on a gate so tests can cancel or release it.
    /// </summary>
    private sealed class FakeLoop : IAgentLoop
    {
        private readonly AgentTurnResult? _result;
        private readonly Exception? _thrown;
        private TaskCompletionSource<AgentTurnResult>? _gate;

        public int RunCount;
        public string? LastRequest;
        public bool LastDryRun;

        public FakeLoop(AgentTurnResult? result = null, Exception? thrown = null) => (_result, _thrown) = (result, thrown);

        /// <summary>Holds the next turn until Release or CancelGate is called.</summary>
        public void Hold() => _gate = new TaskCompletionSource<AgentTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes the held turn with the configured result.</summary>
        public void Release() => _gate?.TrySetResult(_result ?? new AgentTurnResult());

        /// <summary>Completes the held turn as cancelled.</summary>
        public void CancelGate() => _gate?.TrySetCanceled();

        public Task<AgentTurnResult> RunAsync(
            string workspaceRoot, string request, IAgentUserInterface ui,
            bool dryRun = false, CancellationToken cancellationToken = default, bool refreshEnv = false)
        {
            RunCount++;
            LastRequest = request;
            LastDryRun = dryRun;

            if (_thrown is not null)
                return Task.FromException<AgentTurnResult>(_thrown);

            if (_gate is not null)
            {
                cancellationToken.Register(CancelGate);
                return _gate.Task;
            }

            return Task.FromResult((_result ?? new AgentTurnResult()) with { DryRun = dryRun });
        }
    }

    private static AgentTurnResult SuccessfulTurn() => new()
    {
        Branch = "agent/fix",
        Turn = 3,
        Commits = ["fix: login flow"],
        Summary = "The login flow is fixed.",
        CompletedByDone = true,
        DryRun = false,
        ChangeSet = [new ChangedPath("src/a.cs", "modified"), new ChangedPath("src/new.cs", "added")],
        AutoApprovedCommands = 4,
        ConfirmedCommands = 1,
        DiffStat = " src/a.cs | 2 +-",
    };

    [Fact]
    public async Task Send_RunsTheTurn_ShowsSummaryAndChangedFiles_AndClearsTheComposer()
    {
        var (viewModel, feed, loop) = CreateViewModel(SuccessfulTurn());
        viewModel.WorkspaceRoot = @"G:\demo";
        viewModel.RequestText = "fix the login flow";

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsRunning);

        loop.RunCount.Should().Be(1);
        loop.LastRequest.Should().Be("fix the login flow");
        viewModel.RequestText.Should().BeEmpty();
        viewModel.LastSummary.Should().NotBeNull();
        viewModel.LastSummary?.Turn.Should().Be(3);
        viewModel.LastSummary?.Branch.Should().Be("agent/fix");
        viewModel.LastSummary?.Summary.Should().Be("The login flow is fixed.");
        viewModel.ChangedFiles.Select(change => change.Path).Should().Equal("src/a.cs", "src/new.cs");
        feed.Items.OfType<UserRequestItem>().Should().ContainSingle(item => item.Message == "fix the login flow");
        feed.Items.OfType<TurnSummaryItem>().Should().ContainSingle();
    }

    [Fact]
    public async Task Send_PassesTheDryRunToggleToTheLoop()
    {
        var (viewModel, _, loop) = CreateViewModel(SuccessfulTurn());
        viewModel.WorkspaceRoot = @"G:\demo";
        viewModel.RequestText = "check";
        viewModel.DryRun = true;

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsRunning);

        loop.LastDryRun.Should().BeTrue();
        viewModel.LastSummary?.DryRun.Should().BeTrue();
    }

    [Fact]
    public async Task Send_WithErrorResult_ShowsAnErrorItem_AndNoSummary()
    {
        var result = new AgentTurnResult { ErrorMessage = "KA_BROWSER_CONNECTION_FAILED", Turn = 1 };
        var (viewModel, feed, _) = CreateViewModel(result);
        viewModel.WorkspaceRoot = @"G:\demo";
        viewModel.RequestText = "go";

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsRunning);

        viewModel.LastSummary.Should().BeNull();
        feed.Items.OfType<ErrorItem>().Should().ContainSingle(item => item.Message == "KA_BROWSER_CONNECTION_FAILED");
    }

    [Fact]
    public async Task Send_WithStoppedReason_KeepsItOnTheSummary()
    {
        var result = new AgentTurnResult { Turn = 2, Branch = "agent/x", StoppedReason = "iteration limit reached" };
        var (viewModel, feed, _) = CreateViewModel(result);
        viewModel.WorkspaceRoot = @"G:\demo";
        viewModel.RequestText = "go";

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsRunning);

        viewModel.LastSummary.Should().NotBeNull();
        viewModel.LastSummary?.StoppedReason.Should().Be("iteration limit reached");
        feed.Items.OfType<ErrorItem>().Should().BeEmpty();
    }

    [Fact]
    public async Task CancelledTurn_ShowsCancelledError_AndEndsRunning()
    {
        var (viewModel, feed, loop) = CreateViewModel();
        loop.Hold();
        viewModel.WorkspaceRoot = @"G:\demo";
        viewModel.RequestText = "long turn";

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsRunning);
        loop.CancelGate();

        await WaitUntilAsync(() => !viewModel.IsRunning);
        feed.Items.OfType<ErrorItem>().Should().ContainSingle(item => item.Message == "Cancelled");
        viewModel.LastSummary.Should().BeNull();
    }

    [Fact]
    public async Task UnexpectedLoopException_ShowsAnErrorItem_NeverCrashes()
    {
        var (viewModel, feed, _) = CreateViewModel(thrown: new InvalidOperationException("boom"));
        viewModel.WorkspaceRoot = @"G:\demo";
        viewModel.RequestText = "go";

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsRunning);

        feed.Items.OfType<ErrorItem>().Should().ContainSingle(item => item.Message == "InvalidOperationException: boom");
        viewModel.LastSummary.Should().BeNull();
    }

    [Fact]
    public async Task DoubleSend_IsBlocked()
    {
        var (viewModel, _, loop) = CreateViewModel(SuccessfulTurn());
        loop.Hold();
        viewModel.WorkspaceRoot = @"G:\demo";
        viewModel.RequestText = "first";

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsRunning);
        viewModel.RequestText = "second";
        viewModel.SendCommand.Execute(null);

        loop.RunCount.Should().Be(1);

        loop.Release();
        await WaitUntilAsync(() => !viewModel.IsRunning);
    }

    [Fact]
    public async Task Stop_CancelsTheRunningTurn()
    {
        var (viewModel, feed, loop) = CreateViewModel();
        loop.Hold();
        viewModel.WorkspaceRoot = @"G:\demo";
        viewModel.RequestText = "long turn";

        viewModel.SendCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsRunning);
        viewModel.StopCommand.Execute(null);

        await WaitUntilAsync(() => !viewModel.IsRunning);
        loop.RunCount.Should().Be(1);
        feed.Items.OfType<ErrorItem>().Should().ContainSingle(item => item.Message == "Cancelled");
    }

    [Fact]
    public void SendCommand_DisabledWhenInputOrWorkspaceMissing_OrWhileRunning()
    {
        var (viewModel, _, _) = CreateViewModel(SuccessfulTurn());

        viewModel.SendCommand.CanExecute(null).Should().BeFalse();

        viewModel.WorkspaceRoot = @"G:\demo";
        viewModel.SendCommand.CanExecute(null).Should().BeFalse();

        viewModel.RequestText = "go";
        viewModel.SendCommand.CanExecute(null).Should().BeTrue();
    }

    private static (AgentViewModel ViewModel, ActivityFeed Feed, FakeLoop Loop) CreateViewModel(
        AgentTurnResult? result = null, Exception? thrown = null)
    {
        var dispatcher = new ImmediateDispatcher();
        var feed = new ActivityFeed();
        var busy = new BusyIndicator(dispatcher);
        var dialogs = new DialogService(new DialogHostViewModel(), dispatcher);
        var loop = new FakeLoop(result, thrown);
        var runner = new AgentRunner(
            dispatcher,
            feed,
            () => new WpfUserInterface(dispatcher, dialogs, feed, busy),
            (_, _) => loop);
        var viewModel = new AgentViewModel(runner, dispatcher, feed);
        return (viewModel, feed, loop);
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
}
