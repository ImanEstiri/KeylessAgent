namespace ImanSoftware.KeylessAgent.Abstractions.Services;

/// <summary>
/// Records per-phase timings for one agent turn and produces a summary table.
/// Thread-safe: each phase is a single Start/Stop pair.
/// </summary>
public interface ITimingRecorder : IService
{
    /// <summary>Starts timing a named phase (no-op if already started).</summary>
    void Start(string phase);

    /// <summary>Stops timing a named phase and records the elapsed time.</summary>
    void Stop(string phase);

    /// <summary>The recorded phases in start order with their durations.</summary>
    IReadOnlyList<(string Phase, TimeSpan Duration)> Entries { get; }

    /// <summary>Total turn duration (from first Start to last Stop).</summary>
    TimeSpan Total { get; }

    /// <summary>A formatted table of all recorded timings.</summary>
    string FormatTable();
}
