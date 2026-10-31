using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Execution;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Execution;

/// <summary>
/// Timeout and cancellation keep the partial output printed before the kill, and expose
/// WHY the process did not finish ("timeout" vs "cancelled") — a cancelled command is
/// never reported as a timeout.
/// </summary>
public class ProcessRunnerTests
{
    private const string PartialThenHang = "/c echo partial-output & ping -n 30 127.0.0.1 > nul";

    [Fact]
    public async Task Cancelled_KeepsPartialOutput_AndReportsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var runner = new ProcessRunner();
        var task = runner.RunAsync("cmd", PartialThenHang, Path.GetTempPath(), timeoutSeconds: 60, cts.Token);
        cts.CancelAfter(300);

        var result = await task;

        result.Reason.Should().Be("cancelled");
        result.TimedOut.Should().BeFalse();
        result.Output.Should().Contain("partial-output", "stdout printed before the kill is kept");
    }

    [Fact]
    public async Task Timeout_KeepsPartialOutput_AndReportsTimeout()
    {
        var runner = new ProcessRunner();

        var result = await runner.RunAsync("cmd", PartialThenHang, Path.GetTempPath(), timeoutSeconds: 1);

        result.Reason.Should().Be("timeout");
        result.TimedOut.Should().BeTrue();
        result.Output.Should().Contain("partial-output", "stdout printed before the kill is kept");
    }
}
