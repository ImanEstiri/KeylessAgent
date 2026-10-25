using ImanSoftware.DateTimeProvider;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Browser;

/// <summary>
/// A tiny local rate limiter for chat messages: at most N messages per minute,
/// plus a small random human-like delay between browser actions.
/// </summary>
internal sealed class RateLimiter
{
    private readonly int _maxPerMinute;
    private readonly int _minDelayMs;
    private readonly int _maxDelayMs;
    private readonly Random _random;
    private readonly Queue<DateTime> _sentAt = [];

    public RateLimiter(int maxPerMinute, int minDelayMs, int maxDelayMs, Random? random = null)
    {
        _maxPerMinute = Math.Max(1, maxPerMinute);
        _minDelayMs = Math.Max(0, minDelayMs);
        _maxDelayMs = Math.Max(_minDelayMs, maxDelayMs);
        _random = random ?? new Random();
    }

    /// <summary>
    /// Waits until sending one more message is allowed, then records it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        while (_sentAt.Count >= _maxPerMinute)
        {
            var oldest = _sentAt.Peek();
            var release = oldest.AddMinutes(1);
            if (release > now)
            {
                await Task.Delay(release - now, cancellationToken);
                now = DateTime.UtcNow;
            }

            while (_sentAt.Count > 0 && _sentAt.Peek().AddMinutes(1) <= now)
                _sentAt.Dequeue();
        }

        var jitter = _random.Next(_minDelayMs, _maxDelayMs + 1);
        if (jitter > 0)
            await Task.Delay(jitter, cancellationToken);

        _sentAt.Enqueue(DateTime.UtcNow);
    }
}
