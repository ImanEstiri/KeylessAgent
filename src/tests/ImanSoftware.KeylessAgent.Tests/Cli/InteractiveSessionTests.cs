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

    [Fact]
    public async Task CancelAtIdlePrompt_ExitsGracefullyWithZero()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter(""); // deterministic: the prompt would quit anyway
        var session = new InteractiveSession(console,
            (_, _, _) => Task.FromResult(0),
            (_, _, _) => null);

        var runTask = session.RunAsync(@"C:\ws", CancellationToken.None);
        session.RequestCancel(); // Ctrl+C at the idle prompt (the seam the Console.CancelKeyPress handler calls).

        var (exitCode, _) = await runTask;

        exitCode.Should().Be(0, "idle Ctrl+C exits gracefully, not with a crash");
    }

    [Fact]
    public async Task FirstCancelDuringTurn_CancelsOnlyTheTurn_SecondCancelWithinTwoSeconds_Exits()
    {
        var console = new TestConsole();
        console.Input.PushTextWithEnter("a long turn");
        var turnStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turnsCancelled = 0;

        var session = new InteractiveSession(console,
            (_, _, ct) =>
            {
                turnStarted.TrySetResult(true);
                return Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                        return 0;
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref turnsCancelled);
                        throw;
                    }
                });
            },
            (_, _, _) => null);

        var runTask = session.RunAsync(@"C:\ws", CancellationToken.None);
        await turnStarted.Task;

        session.RequestCancel(); // first press: cancel the turn only
        session.RequestCancel(); // second press within 2s: exit gracefully

        var (exitCode, _) = await runTask;

        turnsCancelled.Should().Be(1, "the first press cancelled the running turn");
        exitCode.Should().Be(0, "the second press exited gracefully");
    }
}
