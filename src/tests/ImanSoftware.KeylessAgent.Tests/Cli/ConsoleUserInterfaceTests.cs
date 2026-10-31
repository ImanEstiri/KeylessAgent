using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Testing;

namespace ImanSoftware.KeylessAgent.Tests.Cli;

/// <summary>
/// Reproduces the crash behind the bug report. On a real console, live displays
/// (Status/Progress) and prompts both acquire the console's exclusivity mode;
/// a prompt issued while a live display is active throws
/// "Trying to run one or more interactive functions concurrently…", which killed the CLI
/// whenever a turn (wrapped in a turn-wide Status spinner) hit Say/Confirm/Ask.
/// TestConsole's exclusivity mode is permissive, so this repro simulates the real
/// console's mode and drives the exact nested-acquire sequence the old code produced.
/// </summary>
public class ReproConcurrentLiveDisplayTests
{
    [Fact]
    public void Prompt_WhileLiveDisplayIsActive_ThrowsConcurrentOperationException()
    {
        var mode = new StrictExclusivityMode();

        // turn-wide spinner (holds the exclusivity) + prompt inside (tries to acquire again):
        var act = () => mode.Run(() => mode.Run<int>(() => 0));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*concurrently*");
    }

    /// <summary>
    /// Mimics Spectre's real-console exclusivity mode: one interactive function at a time,
    /// exact production exception otherwise.
    /// </summary>
    private sealed class StrictExclusivityMode : IExclusivityMode
    {
        private int _busy;

        public T Run<T>(Func<T> func)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Trying to run one or more interactive functions concurrently. Operations with dynamic displays (e.g. a prompt and a progress display) cannot be running at the same time.");
            }

            try
            {
                return func();
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        public async Task<T> RunAsync<T>(Func<Task<T>> func)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException(
                    "Trying to run one or more interactive functions concurrently. Operations with dynamic displays (e.g. a prompt and a progress display) cannot be running at the same time.");
            }

            try
            {
                return await func();
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }
    }
}
