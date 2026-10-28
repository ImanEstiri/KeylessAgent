using FluentAssertions;
using ImanSoftware.KeylessAgent.Cli.Helpers;
using ImanSoftware.KeylessAgent.Core.Implementation.Browser;
using Spectre.Console;
using Spectre.Console.Testing;

namespace ImanSoftware.KeylessAgent.Tests.Cli;

/// <summary>
/// The shared CLI turn runner: --timing prints the per-phase timing table after the turn.
/// </summary>
public class AgentCliRunnerTests
{
    [Fact]
    public async Task RunTurnAsync_WithTiming_PrintsTimingSummary()
    {
        var console = new TestConsole();
        var original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        var workspace = Path.Combine(Path.GetTempPath(), "ka-cli-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var chat = new FakeChatProvider(
                "@@say\n<<<MSG\nworking\nMSG>>>\n@@end\n@@done\n<<<MSG\nfinished\nMSG>>>\n@@end\n@@eof\n");

            var exitCode = await AgentCliRunner.RunTurnAsync(
                workspace, "timing test", dryRun: false, verbose: false,
                cancellationToken: CancellationToken.None, timing: true, chatProvider: chat);

            exitCode.Should().Be(0);
            console.Output.Should().Contain("Timing Summary");
            console.Output.Should().Contain("reply-wait");
            console.Output.Should().Contain("TOTAL");
        }
        finally
        {
            AnsiConsole.Console = original;
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RunTurnAsync_WithoutTiming_PrintsNoTimingSummary()
    {
        var console = new TestConsole();
        var original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        var workspace = Path.Combine(Path.GetTempPath(), "ka-cli-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var chat = new FakeChatProvider(
                "@@say\n<<<MSG\nworking\nMSG>>>\n@@end\n@@done\n<<<MSG\nfinished\nMSG>>>\n@@end\n@@eof\n");

            var exitCode = await AgentCliRunner.RunTurnAsync(
                workspace, "no timing test", dryRun: false, verbose: false,
                cancellationToken: CancellationToken.None, chatProvider: chat);

            exitCode.Should().Be(0);
            console.Output.Should().NotContain("Timing Summary");
        }
        finally
        {
            AnsiConsole.Console = original;
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }
}
