using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Wpf.Services;
using ImanSoftware.KeylessAgent.Wpf.Tests.Fakes;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using ImanSoftware.KeylessAgent.Wpf.ViewModels.Dialogs;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Services;

public sealed class DialogServiceTests
{
    private readonly DialogHostViewModel _host = new();
    private readonly DialogService _service;

    public DialogServiceTests()
    {
        _service = new DialogService(_host, new ImmediateDispatcher());
    }

    [Fact]
    public async Task ConfirmAsync_ApproveCompletesWithTrue_AndHidesTheOverlay()
    {
        var task = _service.ConfirmAsync(new ConfirmRequest("Approve write?", "detail"));

        await WaitUntilAsync(() => _host.Current is ConfirmDialogViewModel);
        ((ConfirmDialogViewModel)_host.Current!).ApproveCommand.Execute(null);

        (await task).Should().BeTrue();
        await WaitUntilAsync(() => !_host.HasCurrent);
    }

    [Fact]
    public async Task ConfirmAsync_DeclineCompletesWithFalse()
    {
        var task = _service.ConfirmAsync(new ConfirmRequest("Delete?", "detail"));

        await WaitUntilAsync(() => _host.Current is ConfirmDialogViewModel);
        ((ConfirmDialogViewModel)_host.Current!).DeclineCommand.Execute(null);

        (await task).Should().BeFalse();
    }

    [Fact]
    public async Task TwoConcurrentConfirms_AreShownOneAfterAnother()
    {
        var first = _service.ConfirmAsync(new ConfirmRequest("First", "d1"));
        await WaitUntilAsync(() => _host.Current is ConfirmDialogViewModel);
        var firstViewModel = (ConfirmDialogViewModel)_host.Current!;

        var second = _service.ConfirmAsync(new ConfirmRequest("Second", "d2"));

        _host.Current.Should().BeSameAs(firstViewModel);

        firstViewModel.ApproveCommand.Execute(null);
        (await first).Should().BeTrue();

        await WaitUntilAsync(() => _host.Current is ConfirmDialogViewModel && !ReferenceEquals(_host.Current, firstViewModel));
        var secondViewModel = (ConfirmDialogViewModel)_host.Current;
        secondViewModel.Title.Should().Be("Second");

        secondViewModel.DeclineCommand.Execute(null);
        (await second).Should().BeFalse();
        await WaitUntilAsync(() => !_host.HasCurrent);
    }

    [Fact]
    public async Task CancelAll_CompletesTheShownConfirmWithFalse_AndHidesTheOverlay()
    {
        var task = _service.ConfirmAsync(new ConfirmRequest("Running turn?", "detail"));
        await WaitUntilAsync(() => _host.HasCurrent);

        _service.CancelAll();

        (await task).Should().BeFalse();
        await WaitUntilAsync(() => !_host.HasCurrent);
    }

    [Fact]
    public async Task CancelAll_CompletesQueuedDialogsToo()
    {
        var shown = _service.ConfirmAsync(new ConfirmRequest("First", "d1"));
        await WaitUntilAsync(() => _host.HasCurrent);
        var queued = _service.ConfirmAsync(new ConfirmRequest("Second", "d2"));

        _service.CancelAll();

        (await shown).Should().BeFalse();
        (await queued).Should().BeFalse();
        await WaitUntilAsync(() => !_host.HasCurrent);
    }

    [Fact]
    public async Task AskAsync_SubmitReturnsTheAnswerText()
    {
        var task = _service.AskAsync("Which branch?");
        await WaitUntilAsync(() => _host.Current is AskDialogViewModel);

        var viewModel = (AskDialogViewModel)_host.Current!;
        viewModel.AnswerText = "agent/fix-login";
        viewModel.SubmitCommand.Execute(null);

        (await task).Should().Be("agent/fix-login");
    }

    [Fact]
    public async Task AskAsync_CancelReturnsAnEmptyString()
    {
        var task = _service.AskAsync("Which branch?");
        await WaitUntilAsync(() => _host.Current is AskDialogViewModel);

        var viewModel = (AskDialogViewModel)_host.Current!;
        viewModel.AnswerText = "discarded";
        viewModel.CancelCommand.Execute(null);

        (await task).Should().BeEmpty();
    }

    [Fact]
    public async Task ChooseAsync_DefaultOptionStartsHighlighted()
    {
        var task = _service.ChooseAsync(Menu());
        await WaitUntilAsync(() => _host.Current is ChoiceDialogViewModel);

        var viewModel = (ChoiceDialogViewModel)_host.Current!;
        viewModel.HighlightedNumber.Should().Be(2);
        viewModel.Rows[1].IsSelected.Should().BeTrue();

        viewModel.CancelCommand.Execute(null);
        (await task).Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task ChooseAsync_DigitKeyPicksImmediately()
    {
        var task = _service.ChooseAsync(Menu());
        await WaitUntilAsync(() => _host.Current is ChoiceDialogViewModel);

        ((ChoiceDialogViewModel)_host.Current!).PickDigit(3);

        var result = await task;
        result.SelectedId.Should().Be("abort");
        result.Cancelled.Should().BeFalse();
    }

    [Fact]
    public async Task ChooseAsync_EscCancelsWithNullId()
    {
        var task = _service.ChooseAsync(Menu());
        await WaitUntilAsync(() => _host.Current is ChoiceDialogViewModel);

        ((ChoiceDialogViewModel)_host.Current!).CancelCommand.Execute(null);

        var result = await task;
        result.SelectedId.Should().BeNull();
        result.Cancelled.Should().BeTrue();
    }

    [Fact]
    public async Task ChooseAsync_CancellationTokenCancelsTheShownMenu()
    {
        using var cancellation = new CancellationTokenSource();
        var task = _service.ChooseAsync(Menu(), cancellation.Token);
        await WaitUntilAsync(() => _host.Current is ChoiceDialogViewModel);

        await cancellation.CancelAsync();

        var result = await task;
        result.SelectedId.Should().BeNull();
        result.Cancelled.Should().BeTrue();
    }

    [Fact]
    public void ChoiceDialogViewModel_ArrowMovementClampsAtTheBounds()
    {
        var viewModel = new ChoiceDialogViewModel(Menu(), _ => { });

        viewModel.MoveUp();
        viewModel.HighlightedNumber.Should().Be(1);

        viewModel.MoveDown();
        viewModel.MoveDown();
        viewModel.HighlightedNumber.Should().Be(3);

        viewModel.MoveDown();
        viewModel.HighlightedNumber.Should().Be(3);

        viewModel.MoveUp();
        viewModel.HighlightedNumber.Should().Be(2);
    }

    private static ChoiceRequest Menu() => new(
        "Pick a strategy",
        new List<ChoiceOption>
        {
            new("carry", "Carry changes"),
            new("stash", "Stash changes", "git stash push"),
            new("abort", "Abort"),
        },
        "stash");

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
