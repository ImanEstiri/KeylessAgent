using FluentAssertions;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using Spectre.Console.Testing;

namespace ImanSoftware.KeylessAgent.Tests.Cli;

/// <summary>
/// A failing turn in the interactive loop is reported and the loop returns to the prompt
/// instead of crashing the process (the original bug made any turn-level exception fatal).
/// </summary>
public class InteractiveSessionTests
{
    [Fact]
    public async Task FailingTurn_IsReported_AndLoopReturnsToPrompt()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("do something");
        console.Input.PushTextWithEnter(""); // quit
        var turns = 0;

        var session = new InteractiveSession(console,
            (_, _, _) =>
            {
                turns++;
                throw new InvalidOperationException("KA_BROWSER_CONNECTION_FAILED: Chrome is not running");
            },
            (_, _, _) => null);

        var (exitCode, _) = await session.RunAsync(@"C:\ws", CancellationToken.None);

        turns.Should().Be(1);
        exitCode.Should().Be(0, "a failed turn is not a process failure");
        console.Output.Should().Contain("KA_BROWSER_CONNECTION_FAILED");
        console.Output.Should().Contain(".agent/logs");
        // The loop came back for a second prompt before the (empty) quit input.
        console.Output.Should().Contain("keyless>");
    }

    [Fact]
    public async Task CancelledTurn_ReportsCancellation_AndLoopContinues()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("first request");
        console.Input.PushTextWithEnter(""); // quit
        var turns = 0;

        var session = new InteractiveSession(console,
            (_, _, ct) =>
            {
                turns++;
                throw new OperationCanceledException(ct);
            },
            (_, _, _) => null);

        var (exitCode, _) = await session.RunAsync(@"C:\ws", CancellationToken.None);

        turns.Should().Be(1);
        exitCode.Should().Be(0);
        console.Output.Should().Contain("Turn cancelled.");
    }

    [Fact]
    public async Task EmptyRequest_QuitsWithZero()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("");
        var turns = 0;

        var session = new InteractiveSession(console,
            (_, _, _) =>
            {
                turns++;
                return Task.FromResult(0);
            },
            (_, _, _) => null);

        var (exitCode, _) = await session.RunAsync(@"C:\ws", CancellationToken.None);

        turns.Should().Be(0);
        exitCode.Should().Be(0);
    }
}
