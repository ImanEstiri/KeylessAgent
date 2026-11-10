
















using ImanSoftware.DateTimeProvider;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// A tiny local rate limiter for chat messages: at most N messages per minute, a minimum
/// wall-clock spacing between two consecutive sends, plus a small random human-like delay
/// between browser actions. The spacing floor is what stops a burst of quick batches from
/// tripping the provider's anti-abuse captcha.
/// </summary>
internal sealed class RateLimiter
{
    private readonly int _maxPerMinute;
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    private readonly TimeSpan _minMessageSpacing;
    private readonly Random _random;
    private readonly IDateTimeProvider _time;
    private readonly Queue<DateTime> _sentAt = [];
    private DateTime? _lastSentAt;

    public RateLimiter(
        int maxPerMinute,
        int minDelayMs,
        int maxDelayMs,
        TimeSpan minMessageSpacing,
        IDateTimeProvider time,
        Random? random = null)
    {
        if (minMessageSpacing < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(minMessageSpacing), minMessageSpacing, "The minimum message spacing must not be negative.");

        _maxPerMinute = Math.Max(1, maxPerMinute);
        _minDelayMs = Math.Max(0, minDelayMs);
        _maxDelayMs = Math.Max(_minDelayMs, maxDelayMs);
        _minMessageSpacing = minMessageSpacing;
        _time = time;
        _random = random ?? new Random();
    }

    private DateTime UtcNow => _time.Now.ToUniversalTime();

    /// <summary>
    /// Waits until sending one more message is allowed, then records it. Enforces, in order:
    /// the minimum spacing since the last send, the rolling one-minute burst limit, and the
    /// random jitter. Cancellation flows through every wait.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        // 1. Minimum wall-clock spacing since the previous send (additive, never a replacement).
        if (_lastSentAt is DateTime lastSent)
        {
            var earliestNext = lastSent + _minMessageSpacing;
            var spacingWait = earliestNext - UtcNow;
            if (spacingWait > TimeSpan.Zero)
                await Task.Delay(spacingWait, cancellationToken);
        }

        // 2. Rolling burst limit: at most _maxPerMinute messages per 60 seconds.
        var now = UtcNow;
        while (_sentAt.Count >= _maxPerMinute)
        {
            var oldest = _sentAt.Peek();
            var release = oldest.AddMinutes(1);
            if (release > now)
            {
                await Task.Delay(release - now, cancellationToken);
                now = UtcNow;
            }

            while (_sentAt.Count > 0 && _sentAt.Peek().AddMinutes(1) <= now)
                _sentAt.Dequeue();
        }

        // 3. Human-like jitter.
        var jitter = _random.Next(_minDelayMs, _maxDelayMs + 1);
        if (jitter > 0)
            await Task.Delay(jitter, cancellationToken);

        var recorded = UtcNow;
        _sentAt.Enqueue(recorded);
        _lastSentAt = recorded;
    }
}
