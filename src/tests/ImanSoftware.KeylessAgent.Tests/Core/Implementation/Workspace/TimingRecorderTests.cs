using FluentAssertions;
using ImanSoftware.KeylessAgent.Core.Implementation.Workspace;

namespace ImanSoftware.KeylessAgent.Tests.Core.Implementation.Workspace;

public class TimingRecorderTests
{
    [Fact]
    public void DoubleStart_IsIgnored()
    {
        var recorder = new TimingRecorder();

        recorder.Start("x");
        recorder.Start("x");
        recorder.Stop("x");

        recorder.Entries.Should().ContainSingle(e => e.Phase == "x",
            "the second Start must be ignored so the phase is recorded exactly once");
    }

    [Fact]
    public void DuplicatePhases_AreNumberedInTheTable()
    {
        var recorder = new TimingRecorder();

        recorder.Start("send-confirm");
        recorder.Stop("send-confirm");
        recorder.Start("send-confirm");
        recorder.Stop("send-confirm");

        var table = recorder.FormatTable();
        table.Should().Contain("send-confirm #1");
        table.Should().Contain("send-confirm #2");
    }

    [Fact]
    public void Table_ShowsWallClock_AndWithoutUserWait()
    {
        var recorder = new TimingRecorder();

        recorder.Start("execute");
        recorder.Stop("execute");
        recorder.Start(TimingRecorder.UserWaitPhase);
        recorder.Stop(TimingRecorder.UserWaitPhase);

        var table = recorder.FormatTable();
        table.Should().Contain("TOTAL (wall clock)");
        table.Should().Contain("TOTAL without user-wait");

        recorder.TotalWithoutUserWait.Should().BeLessThanOrEqualTo(recorder.Total);
        recorder.TotalWithoutUserWait.Should().BePositive("the execute phase ran without any user wait");
    }

    [Fact]
    public void UserWaitPhase_IsExcludedFromWithoutUserWait_ButCountedInTotal()
    {
        var recorder = new TimingRecorder();

        recorder.Start("execute");
        System.Threading.Thread.Sleep(50);
        recorder.Stop("execute");

        recorder.Start(TimingRecorder.UserWaitPhase);
        System.Threading.Thread.Sleep(100);
        recorder.Stop(TimingRecorder.UserWaitPhase);

        recorder.TotalWithoutUserWait.Should().BeLessThan(recorder.Total,
            "the user-wait share must be subtracted from the wall-clock total");
        recorder.Total.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(150);
    }

    [Fact]
    public void StopWithoutStart_IsIgnored()
    {
        var recorder = new TimingRecorder();

        recorder.Stop("never-started");

        recorder.Entries.Should().BeEmpty();
    }

    [Fact]
    public void SequentialPhases_EachRecorded()
    {
        var recorder = new TimingRecorder();

        recorder.Start("a");
        recorder.Stop("a");
        recorder.Start("a");
        recorder.Stop("a");

        recorder.Entries.Count(e => e.Phase == "a").Should().Be(2,
            "sequential (non-overlapping) Start/Stop pairs all count");
    }
}
