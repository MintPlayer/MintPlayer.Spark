using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace MintPlayer.Spark.Mcp.Http;

/// <summary>
/// Calls per minute per signed-in user and per address for anonymous calls (MCP PRD D16).
/// </summary>
/// <remarks>
/// Self-contained, like the share and mail-bounce endpoints: a second <c>UseRateLimiter()</c> would silently
/// halve the application's own budget (it has no idempotence marker), so the endpoint meters itself on top of
/// whatever limiter the application runs. In memory, per process: a call counter is not worth a database write
/// per call, which is what the persistent quota mechanism (<c>SparkQuota</c>) costs.
/// </remarks>
internal sealed class McpRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter;

    public McpRateLimiter(SparkMcpOptions options)
    {
        var signedIn = options.Quota.SignedInPerMinute;
        var anonymous = options.Quota.AnonymousPerMinute;
        limiter = PartitionedRateLimiter.Create<string, string>(key =>
        {
            var permits = key.StartsWith("user:", StringComparison.Ordinal) ? signedIn : anonymous;
            return permits <= 0
                ? RateLimitPartition.GetNoLimiter(key)
                : RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
        });
    }

    /// <summary>Counts this call against the caller's partition; <see langword="false"/> when the limit is reached.</summary>
    public bool TryAcquire(HttpContext context, bool signedIn)
    {
        using var lease = limiter.AttemptAcquire(KeyFor(context, signedIn));
        return lease.IsAcquired;
    }

    private static string KeyFor(HttpContext context, bool signedIn)
    {
        if (signedIn)
        {
            var user = context.User;
            var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? user.Identity?.Name;
            if (!string.IsNullOrEmpty(id))
                return "user:" + id;
        }

        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    public void Dispose() => limiter.Dispose();
}
