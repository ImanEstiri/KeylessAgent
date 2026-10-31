using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Abstractions.Models.Enums;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Dialogs;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Feed;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Services;

public sealed class WpfUserInterfaceTests
{
    private readonly DialogHostViewModel _host = new();
    private readonly ActivityFeed _feed = new();
    private readonly BusyIndicator _busy;
    private readonly WpfUserInterface _ui;

    public WpfUserInterfaceTests()
    {
        var dispatcher = new ImmediateDispatcher();
        _busy = new BusyIndicator(dispatcher);
        _ui = new WpfUserInterface(dispatcher, new DialogService(_host, dispatcher), _feed, _busy);
    }

    [Fact]
    public async Task Say_FromABackgroundThread_EndsUpInTheFeed()
    {
        await Task.Run(() => _ui.Say("hello from the loop"));

        await WaitUntilAsync(() => _feed.Items.OfType<ModelMessageItem>().Any(item => item.Message == "hello from the loop"));
        _feed.Items.OfType<ModelMessageItem>().Should().Contain(item => item.Message == "hello from the loop");
    }

    [Fact]
    public void ShowProgress_AddsAStepItemToTheFeed()
    {
        _ui.ShowProgress(new AgentProgressEvent(AgentEventType.ConnectingToBrowser, "port 9222"));

        var step = _feed.Items.OfType<StepItem>().Single();
        step.Type.Should().Be(AgentEventType.ConnectingToBrowser);
        step.Detail.Should().Be("port 9222");
    }

    [Fact]
    public void ShowDiff_AddsADiffItemToTheFeed()
    {
        var change = new FileChange("src/a.cs", FileChangeKind.Update, "old", "new");

        _ui.ShowDiff(change);

        _feed.Items.OfType<DiffItem>().Single().Change.Should().Be(change);
    }

    [Fact]
    public void BeginWait_NestsMessagesInTheRightOrder_AndToleratesDoubleDispose()
    {
        var outer = _ui.BeginWait("Connecting to Chrome");
        _busy.CurrentMessage.Should().Be("Connecting to Chrome");
        _busy.IsBusy.Should().BeTrue();

        var inner = _ui.BeginWait("Waiting for the model");
        _busy.CurrentMessage.Should().Be("Waiting for the model");

        inner.Dispose();
        _busy.CurrentMessage.Should().Be("Connecting to Chrome");

        outer.Dispose();
        outer.Dispose();
        _busy.CurrentMessage.Should().BeNull();
        _busy.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void BeginWait_WithEmptyMessage_FallsBackToWaiting()
    {
        using var scope = _ui.BeginWait("   ");
        _busy.CurrentMessage.Should().Be("Waiting…");
    }

    [Fact]
    public async Task ConfirmAsync_DelegatesToTheDialogService()
    {
        var task = _ui.ConfirmAsync(new ConfirmRequest("Approve write?", "detail"));

        await WaitUntilAsync(() => _host.Current is ConfirmDialogViewModel);
        ((ConfirmDialogViewModel)_host.Current!).ApproveCommand.Execute(null);

        (await task).Should().BeTrue();
    }

    [Fact]
    public async Task AskAsync_DelegatesToTheDialogService()
    {
        var task = _ui.AskAsync("Which branch?");
        await WaitUntilAsync(() => _host.Current is AskDialogViewModel);

        var viewModel = (AskDialogViewModel)_host.Current!;
        viewModel.AnswerText = "main";
        viewModel.SubmitCommand.Execute(null);

        (await task).Should().Be("main");
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
