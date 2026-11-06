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
            console.Output.Should().Contain("Context: ", "the size report travels in the progress stream");
            console.Output.Should().Contain(" KB");
            console.Output.Should().Contain("Timing Summary");
            console.Output.Should().Contain("reply-wait");
            console.Output.Should().Contain("TOTAL");
            console.Output.Should().Contain("Auto-approved: 0 | Confirmed: 0",
                "the turn ran no runcommand commands");
            console.Output.Should().Contain("Merge the agent branch yourself");
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

    [Fact]
    public async Task StoppedTurn_Returns2()
    {
        var console = new TestConsole();
        var original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        var workspace = Path.Combine(Path.GetTempPath(), "ka-cli-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            // A reply without @@done/@@next: the batch is reported, the model never completes
            // the turn, and the fake runs out of replies — a classic early stop.
            var chat = new FakeChatProvider(
                "@@say\n<<<MSG\nhalfway\nMSG>>>\n@@end\n@@eof\n");

            var exitCode = await AgentCliRunner.RunTurnAsync(
                workspace, "stop test", dryRun: false, verbose: false,
                cancellationToken: CancellationToken.None, chatProvider: chat);

            exitCode.Should().Be(2, "a turn that stopped early is not a successful one");
            console.Output.Should().Contain("Turn stopped early");
        }
        finally
        {
            AnsiConsole.Console = original;
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task FailedTurn_Returns1()
    {
        var console = new TestConsole();
        var original = AnsiConsole.Console;
        AnsiConsole.Console = console;
        var workspace = Path.Combine(Path.GetTempPath(), "ka-cli-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "keyless.json"), "{ not valid json }");

            var exitCode = await AgentCliRunner.RunTurnAsync(
                workspace, "broken config", dryRun: false, verbose: false,
                cancellationToken: CancellationToken.None, chatProvider: new FakeChatProvider());

            exitCode.Should().Be(1, "a turn that failed before any model interaction");
        }
        finally
        {
            AnsiConsole.Console = original;
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task SuccessfulTurn_Returns0()
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
                workspace, "happy test", dryRun: false, verbose: false,
                cancellationToken: CancellationToken.None, chatProvider: chat);

            exitCode.Should().Be(0);
        }
        finally
        {
            AnsiConsole.Console = original;
            try { Directory.Delete(workspace, recursive: true); } catch { /* best-effort */ }
        }
    }
}
