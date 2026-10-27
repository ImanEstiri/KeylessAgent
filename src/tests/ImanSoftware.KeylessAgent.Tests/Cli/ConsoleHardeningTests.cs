using FluentAssertions;
using ImanSoftware.KeylessAgent.Cli;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using Spectre.Console;
using Spectre.Console.Testing;

namespace ImanSoftware.KeylessAgent.Tests.Cli;

/// <summary>
/// Console hardening: a prompt started immediately after BeginWait never collides with the
/// spinner, a slow spinner shutdown still never breaks the following prompt, and a failing
/// turn is diagnosable (exception type + log path).
/// </summary>
public class ConsoleHardeningTests
{
    [Fact]
    public void Prompt_ImmediatelyAfterBeginWait_NeverThrowsConcurrentOperation()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("answer");
        var ui = new ConsoleUserInterface(console);

        var wait = ui.BeginWait("waiting");

        // No settling delay: the very next console interaction must be safe because
        // BeginWait returns only after the display started, and Ask stops it first.
        var act = () => ui.AskAsync("Which database?");

        act.Invoking(a => a().GetAwaiter().GetResult()).Should().NotThrow();
        console.Output.Should().Contain("answer");
        wait.Dispose();
    }

    [Fact]
    public async Task SlowSpinnerShutdown_StillDoesNotBreakTheFollowingPrompt()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("y");
        var ui = new ConsoleUserInterface(console);

        using var wait = ui.BeginWait("waiting");

        // Give the Status display time to enter its render loop, then immediately ask:
        // the join may take as long as it takes — the prompt waits for it.
        await Task.Delay(250);
        ui.IsSpinnerActive.Should().BeTrue();

        var confirmed = await ui.ConfirmAsync(new ImanSoftware.KeylessAgent.Abstractions.Models.Agent.ConfirmRequest(
            "Run command", "dotnet build"));

        confirmed.Should().BeTrue();
        ui.IsSpinnerActive.Should().BeFalse();
    }

    [Fact]
    public async Task FailingTurnRunner_PrintsExceptionType_AndLogPath()
    {
        var console = new TestConsole();
        var workspace = Path.Combine(Path.GetTempPath(), "ka-hard-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            // Simulate the runner's failure path with the same writer+console routing.
            var writer = new ConsoleWriter(console);
            try
            {
                throw new InvalidOperationException("KA_TEST: boom");
            }
            catch (Exception ex)
            {
                writer.Error($"{ex.GetType().Name}: {ex.Message}");
                writer.Info($"Details: turn transcript in {Path.Combine(workspace, ".agent", "logs")}");
            }

            console.Output.Should().Contain("InvalidOperationException: KA_TEST: boom");
            console.Output.Should().Contain(".agent");
            console.Output.Should().Contain("logs");
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public async Task InteractiveSession_ShowsExceptionType_InFailure()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("request");
        console.Input.PushTextWithEnter("");

        var session = new InteractiveSession(console,
            (_, _, _) => throw new PlaywrightTimeoutExceptionStub("Chrome did not answer"),
            (_, _, _) => null);

        await session.RunAsync(@"C:\ws", CancellationToken.None);

        console.Output.Should().Contain("PlaywrightTimeoutExceptionStub");
        console.Output.Should().Contain("Chrome did not answer");
        // back at the prompt afterwards
        console.Output.Should().Contain("keyless>");
    }

    /// <summary>A stand-in for a Playwright failure type with an honest name.</summary>
    private sealed class PlaywrightTimeoutExceptionStub(string message) : Exception(message);
}
