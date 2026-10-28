using System.Collections.Concurrent;
using System.Diagnostics;

using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Workspace;

/// <summary>
/// Simple thread-safe per-phase timing recorder. Start(phase) opens a Stopwatch,
/// Stop(phase) records the elapsed time.
/// </summary>
internal sealed class TimingRecorder : ITimingRecorder
{
    private readonly ConcurrentDictionary<string, Stopwatch> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Phase, TimeSpan Duration)> _entries = new();

    private DateTimeOffset _firstStart = DateTimeOffset.MaxValue;
    private DateTimeOffset _lastStop = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public void Start(string phase)
    {
        var sw = new Stopwatch();
        _running[phase] = sw;
        sw.Start();
        if (DateTimeOffset.UtcNow < _firstStart)
            _firstStart = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public void Stop(string phase)
    {
        if (!_running.TryRemove(phase, out var sw))
            return;

        sw.Stop();
        _entries.Enqueue((phase, sw.Elapsed));
        _lastStop = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Phase, TimeSpan Duration)> Entries => _entries.ToList();

    /// <inheritdoc />
    public TimeSpan Total =>
        _firstStart == DateTimeOffset.MaxValue || _lastStop == DateTimeOffset.MinValue
            ? TimeSpan.Zero
            : _lastStop - _firstStart;

    /// <inheritdoc />
    public string FormatTable()
    {
        if (_entries.IsEmpty)
            return "No timings recorded.";

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Timing Summary");
        builder.AppendLine("─────────────────────────────────────────");
        foreach (var (phase, duration) in _entries)
        {
            builder.AppendLine($"  {phase,-30} {duration.TotalMilliseconds,8:F0} ms");
        }

        builder.AppendLine("─────────────────────────────────────────");
        builder.AppendLine($"  {"TOTAL",-30} {Total.TotalMilliseconds,8:F0} ms");
        return builder.ToString();
    }
}
