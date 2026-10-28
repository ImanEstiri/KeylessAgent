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
