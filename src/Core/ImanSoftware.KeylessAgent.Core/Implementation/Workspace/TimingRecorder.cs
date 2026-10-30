using System.Collections.Concurrent;
using System.Diagnostics;

using ImanSoftware.KeylessAgent.Abstractions.Services;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Workspace;

/// <summary>
/// Simple thread-safe per-phase timing recorder. Start(phase) opens a Stopwatch,
/// Stop(phase) records the elapsed time. Re-entrant Start is ignored.
/// User-wait time (approvals, menus, @@ask) is recorded as its own phase so the
/// summary can show the turn both as wall clock and without user waits.
/// </summary>
internal sealed class TimingRecorder : ITimingRecorder
{
    public const string UserWaitPhase = "user-wait";

    private readonly ConcurrentDictionary<string, Stopwatch> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Phase, TimeSpan Duration)> _entries = new();

    private DateTimeOffset _firstStart = DateTimeOffset.MaxValue;
    private DateTimeOffset _lastStop = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public void Start(string phase)
    {
        // A second Start for an already-running phase would orphan the first stopwatch
        // (the overwrite loses its entry and Stop would close the wrong one); re-entry
        // is ignored instead so one Start/Stop pair per phase is always recorded.
        if (_running.TryGetValue(phase, out _))
            return;

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

    /// <summary>
    /// Wall-clock total minus the accumulated user-wait time — the time the agent
    /// actually spent working.
    /// </summary>
    public TimeSpan TotalWithoutUserWait
    {
        get
        {
            var userWait = TimeSpan.FromTicks(_entries.Where(e => e.Phase == UserWaitPhase).Sum(e => e.Duration.Ticks));
            return Total - userWait < TimeSpan.Zero ? TimeSpan.Zero : Total - userWait;
        }
    }

    /// <inheritdoc />
    public string FormatTable()
    {
        if (_entries.IsEmpty)
            return "No timings recorded.";

        // Duplicate phase names are numbered (send-confirm #1, #2 …) so concurrent or
        // repeated phases stay readable and sortable.
        // Pre-count so a phase that appears more than once gets ALL its rows numbered
        // (#1..#N); single-occurrence phases keep their plain name.
        var counts = _entries.GroupBy(e => e.Phase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = _entries.Select(entry =>
        {
            var total = counts[entry.Phase];
            string display;
            if (total == 1)
            {
                display = entry.Phase;
            }
            else
            {
                var n = seen.GetValueOrDefault(entry.Phase) + 1;
                seen[entry.Phase] = n;
                display = $"{entry.Phase} #{n}";
            }

            return (display, entry.Duration);
        }).ToList();

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Timing Summary");
        builder.AppendLine("─────────────────────────────────────────");
        foreach (var (display, duration) in rows)
            builder.AppendLine($"  {display,-30} {duration.TotalMilliseconds,8:F0} ms");

        builder.AppendLine("─────────────────────────────────────────");
        builder.AppendLine($"  {"TOTAL (wall clock)",-30} {Total.TotalMilliseconds,8:F0} ms");
        builder.AppendLine($"  {"TOTAL without user-wait",-30} {TotalWithoutUserWait.TotalMilliseconds,8:F0} ms");
        return builder.ToString();
    }
}
