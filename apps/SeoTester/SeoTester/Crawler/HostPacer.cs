namespace SeoTester.Crawler;

/// <summary>
/// Politeness towards the one host a run crawls (<c>docs/seo_analyzer_PRD.md</c> §6, research §4.2): at most
/// <c>connections</c> requests at a time, and request starts at least <c>interval</c> apart (the larger of the tier's
/// interval and the site's <c>Crawl-delay</c>). A <c>Retry-After</c> pushes the next start out. The start of the last
/// request carries over between batches through the frontier, so a new batch does not burst.
/// </summary>
public sealed class HostPacer : IDisposable
{
    private readonly SemaphoreSlim slots;
    private readonly TimeSpan interval;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private DateTimeOffset next;

    public HostPacer(int connections, TimeSpan interval, DateTime? lastRequestAtUtc, TimeProvider time)
    {
        slots = new SemaphoreSlim(Math.Max(1, connections));
        this.interval = interval < TimeSpan.Zero ? TimeSpan.Zero : interval;
        this.time = time;
        next = lastRequestAtUtc is { } last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) + this.interval : DateTimeOffset.MinValue;
    }

    /// <summary>When the latest request started.</summary>
    public DateTime? LastStartUtc { get; private set; }

    /// <summary>Waits for a connection slot and the next allowed start; dispose the result when the request is done.</summary>
    public async Task<IDisposable> WaitAsync(CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset start;
            lock (gate)
            {
                var now = time.GetUtcNow();
                start = next > now ? next : now;
                next = start + interval;
                LastStartUtc = start.UtcDateTime;
            }
            var wait = start - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, time, cancellationToken);
            // A timer fires late, never early: count the interval from when the request really starts, or two starts
            // can end up closer than the interval (measured: 138 ms apart at a 150 ms interval before this).
            lock (gate)
            {
                var actual = time.GetUtcNow();
                if (actual + interval > next)
                    next = actual + interval;
                LastStartUtc = actual.UtcDateTime;
            }
            return new Release(slots);
        }
        catch
        {
            slots.Release();
            throw;
        }
    }

    /// <summary>The server asked to wait (<c>Retry-After</c>): no request starts before then.</summary>
    public void Defer(TimeSpan wait)
    {
        lock (gate)
        {
            var until = time.GetUtcNow() + wait;
            if (until > next)
                next = until;
        }
    }

    public void Dispose() => slots.Dispose();

    private sealed class Release(SemaphoreSlim slots) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                slots.Release();
        }
    }
}
