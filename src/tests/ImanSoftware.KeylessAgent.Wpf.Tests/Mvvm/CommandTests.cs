using FluentAssertions;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;
using ImanSoftware.KeylessAgent.Wpf.ViewModels;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests.Mvvm;

public sealed class CommandTests
{
    [Fact]
    public void RelayCommand_NullExecute_Throws()
        => FluentActions.Invoking(() => new RelayCommand(null!)).Should().Throw<ArgumentNullException>();

    [Fact]
    public void RelayCommand_Execute_RunsAction()
    {
        var ran = false;
        var command = new RelayCommand(() => ran = true);

        command.Execute(null);

        ran.Should().BeTrue();
    }

    [Fact]
    public void RelayCommand_CanExecute_UsesPredicate()
    {
        var allowed = false;
        var command = new RelayCommand(() => { }, () => allowed);

        command.CanExecute(null).Should().BeFalse();
        allowed = true;
        command.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void RelayCommand_WithoutPredicate_CanAlwaysExecute()
        => new RelayCommand(() => { }).CanExecute(null).Should().BeTrue();

    [Fact]
    public void RelayCommand_NotifyCanExecuteChanged_FiresEvent()
    {
        var raised = false;
        var command = new RelayCommand(() => { });
        command.CanExecuteChanged += (_, _) => raised = true;

        command.NotifyCanExecuteChanged();

        raised.Should().BeTrue();
    }

    [Fact]
    public void RelayCommandOfT_Execute_PassesTypedParameter()
    {
        PageKey? received = null;
        var command = new RelayCommand<PageKey>(key => received = key);

        command.Execute(PageKey.About);

        received.Should().Be(PageKey.About);
    }

    [Fact]
    public void RelayCommandOfT_NullParameter_PassesNull()
    {
        string? received = "untouched";
        var command = new RelayCommand<string>(value => received = value);

        command.Execute(null);

        received.Should().BeNull();
    }

    [Fact]
    public void RelayCommandOfT_NonMatchingParameter_PassesDefault()
    {
        string? received = "untouched";
        var command = new RelayCommand<string>(value => received = value);

        command.Execute(42);

        received.Should().BeNull();
    }

    [Fact]
    public void RelayCommandOfT_CanExecute_ReceivesParameter()
    {
        var command = new RelayCommand<int>(_ => { }, value => value > 0);

        command.CanExecute(1).Should().BeTrue();
        command.CanExecute(-1).Should().BeFalse();
    }

    [Fact]
    public void AsyncRelayCommand_NullExecute_Throws()
        => FluentActions.Invoking(() => new AsyncRelayCommand(null!)).Should().Throw<ArgumentNullException>();

    [Fact]
    public async Task AsyncRelayCommand_Execute_RunsOperationAndReportsRunningState()
    {
        var started = new TaskCompletionSource();
        var finished = new TaskCompletionSource();
        var runningChanges = new List<bool>();
        var command = new AsyncRelayCommand(async () =>
        {
            started.SetResult();
            await finished.Task;
        });
        command.IsRunningChanged += (_, e) => runningChanges.Add(e.IsRunning);

        command.Execute(null);
        command.IsRunning.Should().BeTrue();
        await started.Task;

        finished.SetResult();
        await WaitUntilAsync(() => !command.IsRunning);

        runningChanges.Should().Equal(true, false);
    }

    [Fact]
    public async Task AsyncRelayCommand_IgnoresExecuteWhileRunning()
    {
        var gate = new TaskCompletionSource();
        var runs = 0;
        var command = new AsyncRelayCommand(async () =>
        {
            runs++;
            await gate.Task;
        });

        command.Execute(null);
        command.Execute(null);
        runs.Should().Be(1);

        gate.SetResult();
        await WaitUntilAsync(() => !command.IsRunning);

        command.Execute(null);
        runs.Should().Be(2);
        await WaitUntilAsync(() => !command.IsRunning);
    }

    [Fact]
    public async Task AsyncRelayCommand_CanExecute_IsFalseWhileRunning()
    {
        var gate = new TaskCompletionSource();
        var command = new AsyncRelayCommand(async () => await gate.Task);

        command.Execute(null);
        command.CanExecute(null).Should().BeFalse();

        gate.SetResult();
        await WaitUntilAsync(() => !command.IsRunning);
        command.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void AsyncRelayCommand_CanExecutePredicate_IsRespected()
    {
        var allowed = false;
        var command = new AsyncRelayCommand(() => Task.CompletedTask, () => allowed);

        command.CanExecute(null).Should().BeFalse();
        allowed = true;
        command.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task AsyncRelayCommand_Exception_IsReportedThroughOnError()
    {
        var gate = new TaskCompletionSource();
        var errors = new List<Exception>();
        var command = new AsyncRelayCommand(async () =>
        {
            await gate.Task;
            throw new InvalidOperationException("boom");
        }, onError: errors.Add);

        command.Execute(null);
        gate.SetResult();

        await WaitUntilAsync(() => errors.Count == 1);
        command.IsRunning.Should().BeFalse();
        errors.Single().Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("boom");
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
