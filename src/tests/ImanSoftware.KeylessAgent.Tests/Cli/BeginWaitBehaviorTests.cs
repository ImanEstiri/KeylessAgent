using FluentAssertions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Agent;
using ImanSoftware.KeylessAgent.Cli;
using Spectre.Console.Testing;

namespace ImanSoftware.KeylessAgent.Tests.Cli;

/// <summary>
/// Behavioral tests for the fixed console UI: no prompt or output ever runs while the
/// wait spinner (a live display) is active, and overlapping waits are safe.
/// </summary>
public class BeginWaitBehaviorTests
{
    private static ConsoleUserInterface CreateUi(TestConsole console) => new(console);

    private static async Task WaitUntilSpinnerActiveAsync(ConsoleUserInterface ui, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!ui.IsSpinnerActive)
        {
            await Task.Delay(25);
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Spinner did not become active in time.");
        }
    }

    [Fact]
    public async Task ConfirmAsync_DuringActiveWait_StopsSpinnerFirstAndDoesNotThrow()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("y");
        var ui = CreateUi(console);

        using (ui.BeginWait("waiting for the model"))
        {
            await WaitUntilSpinnerActiveAsync(ui, TimeSpan.FromSeconds(5));

            var confirmed = await ui.ConfirmAsync(new ConfirmRequest("Run command", "dotnet build"));

            confirmed.Should().BeTrue();
            ui.IsSpinnerActive.Should().BeFalse("the spinner must be stopped before a prompt runs");
        }
    }

    [Fact]
    public async Task Say_DuringActiveWait_Works()
    {
        var console = new TestConsole();
        var ui = CreateUi(console);

        using (ui.BeginWait("waiting for the model"))
        {
            await WaitUntilSpinnerActiveAsync(ui, TimeSpan.FromSeconds(5));

            ui.Say("hello from the middle of a wait");

            ui.IsSpinnerActive.Should().BeFalse();
            console.Output.Should().Contain("hello from the middle of a wait");
        }
    }

    [Fact]
    public async Task OverlappingBeginWaitCalls_AreSafe()
    {
        var console = new TestConsole();
        var ui = CreateUi(console);

        var first = ui.BeginWait("first wait");
        var second = ui.BeginWait("second wait (replaces the first)");

        await Task.Delay(200);

        var act = () =>
        {
            second.Dispose();
            first.Dispose();
        };
        act.Should().NotThrow();
        ui.IsSpinnerActive.Should().BeFalse();
    }

    [Fact]
    public async Task AskAsync_AfterADisposedWait_PromptsNormally()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("use SQL Server");
        var ui = CreateUi(console);

        var wait = ui.BeginWait("waiting");
        await WaitUntilSpinnerActiveAsync(ui, TimeSpan.FromSeconds(5));
        wait.Dispose();

        var answer = await ui.AskAsync("Which database?");

        answer.Should().Be("use SQL Server");
        ui.IsSpinnerActive.Should().BeFalse();
    }
}
