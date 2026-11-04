using System.Threading.RateLimiting;

namespace MintPlayer.Spark.Search;

/// <summary>
/// Requests per client address to the search endpoints (<c>docs/spark_search_PRD.md</c> D10), which are anonymous and
/// fire per keystroke. Self-contained, like <c>McpRateLimiter</c>: a second <c>UseRateLimiter()</c> would halve the
/// application's own budget. In memory, per process.
/// </summary>
internal sealed class SearchRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter;

    public SearchRateLimiter(SparkSearchOptions options)
    {
        limiter = PartitionedRateLimiter.Create<string, string>(key => options.RateLimit <= 0
            ? RateLimitPartition.GetNoLimiter(key)
            : RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = options.RateLimit,
                Window = options.RateLimitWindow,
                QueueLimit = 0,
            }));
    }

    /// <summary>Counts this request; <see langword="false"/> when the caller's limit is reached.</summary>
    public bool TryAcquire(HttpContext context)
    {
        using var lease = limiter.AttemptAcquire(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}
