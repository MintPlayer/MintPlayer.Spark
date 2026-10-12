using System.Threading.RateLimiting;
using MintPlayer.Spark.IdentityProvider.Configuration;

namespace MintPlayer.Spark.IdentityProvider.Mcp;

/// <summary>
/// The per-address budget for what an anonymous caller can make this server do for an agent client: register one
/// (RFC 7591 without an initial access token) or fetch its metadata document (MCP PRD D5, "rate-limited per IP").
/// </summary>
/// <remarks>
/// In-process and always on, unlike the endpoint rate-limit policies, which act only when the application runs the
/// rate-limiting middleware: this budget is part of the SSRF and storage guard, so it must not depend on wiring.
/// Per node; a fetch served from the freshness cache costs nothing.
/// </remarks>
internal sealed class OidcAgentRegistrationLimiter(SparkIdentityProviderOptions options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(address =>
        RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, options.Mcp?.RegistrationsPerHour ?? 1),
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
        }));

    /// <summary>Takes one permit for <paramref name="address"/>; <see langword="false"/> when its budget is spent.</summary>
    public bool TryAcquire(string? address)
    {
        if (options.Mcp is not { RegistrationsPerHour: > 0 })
            return true;
        using var lease = limiter.AttemptAcquire(address ?? "unknown");
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}
