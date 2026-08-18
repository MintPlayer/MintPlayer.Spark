using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Extensions;

/// <summary>
/// Optional rate-limiter wiring for Spark-powered apps. Security audit finding L-3 keeps
/// the framework itself out of rate-limiting policy — apps opt in. This extension hangs off
/// <see cref="ISparkBuilder"/> so it composes with the rest of the Spark builder surface:
/// <code>
/// builder.Services.AddSpark(spark => spark.AddRateLimiter());
/// </code>
/// or via <c>SparkFullOptions.RateLimiter</c> for AddSparkFull consumers.
///
/// The limiter is partitioned by client IP and scoped to
/// <see cref="SparkRateLimiterOptions.PathPrefixes"/> — by default <c>/spark</c> and <c>/connect</c>,
/// so static assets and app-specific routes remain unthrottled. Over-limit requests are rejected
/// with HTTP 429.
/// </summary>
public static class SparkBuilderRateLimiterExtensions
{
    /// <summary>
    /// Registers a fixed-window rate limiter for Spark endpoints. Calling with no configurator
    /// uses the documented defaults (<see cref="SparkRateLimiterOptions"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Do not also call <c>app.UseRateLimiter()</c>.</b> This is not a style preference — it
    /// silently halves the configured budget. <c>UseRateLimiter</c> has no idempotence marker (unlike
    /// <c>UseRouting</c>, it does not detect a previous registration and return early), and
    /// <c>RateLimitingMiddleware</c> records nothing on the request to say it already ran. Two
    /// registrations therefore means <b>every request acquires two leases from the same partition</b>,
    /// so an app configured for 150 requests per window gets 75, with no error and no log entry. The
    /// only symptom is 429s arriving about twice as often as expected, which reads as a bad traffic
    /// estimate rather than a duplicated middleware.
    /// </para>
    /// <para>
    /// Spark cannot detect this and warn: a manual <c>UseRateLimiter()</c> is a call on the caller's
    /// own <see cref="IApplicationBuilder"/>, invisible at startup, and the middleware leaves no
    /// runtime trace to compare against. To meter additional routes, add them to
    /// <see cref="SparkRateLimiterOptions.PathPrefixes"/>; for a different budget on one route, attach
    /// a named ASP.NET policy to that endpoint. Both compose with this limiter; a second
    /// <c>UseRateLimiter()</c> does not.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <see cref="SparkRateLimiterOptions.PathPrefixes"/> is empty or contains only blank entries. A
    /// limiter that meters nothing is a security control that silently does nothing, so this fails at
    /// startup rather than at the first flood.
    /// </exception>
    public static ISparkBuilder AddRateLimiter(
        this ISparkBuilder builder,
        Action<SparkRateLimiterOptions>? configure = null)
    {
        var options = new SparkRateLimiterOptions();
        configure?.Invoke(options);

        // Normalized once here rather than per request: the prefixes cannot change after startup,
        // and StartsWithSegments wants a leading slash and no trailing one. Making the caller know
        // that would be a trap, not an interface.
        var prefixes = NormalizePrefixes(options.PathPrefixes);

        builder.Services.AddRateLimiter(rl =>
        {
            rl.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                // Only the configured prefixes are throttled. SPA static files, controllers, and any
                // application endpoints outside them remain unmetered so the limiter never starves
                // browser asset loads.
                //
                // /connect is in the defaults alongside /spark because it carries the interactive
                // login, two-factor and consent pages. Scoping to /spark alone meant an app that
                // opted into the limiter still shipped an unthrottled password endpoint, and lockout
                // — which is per-account — does nothing against an attacker spreading attempts
                // across many accounts.
                var path = httpContext.Request.Path;
                if (!MatchesAnyPrefix(path, prefixes))
                    return RateLimitPartition.GetNoLimiter("no-limit");

                // The partition key is the caller alone, so every metered prefix shares one bucket.
                // The budget is a per-caller allowance rather than a per-route one; a route needing
                // its own cost declares a named policy instead.
                var clientKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: clientKey,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.PermitLimit,
                        Window = options.Window,
                        QueueLimit = 0,
                    });
            });
            rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        builder.Registry.AddMiddleware(app => app.UseRateLimiter());

        return builder;
    }

    private static bool MatchesAnyPrefix(PathString path, PathString[] prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (path.StartsWithSegments(prefix))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Accepts prefixes in whatever shape the caller wrote them — <c>"api/browse"</c>,
    /// <c>"/api/browse"</c>, <c>"/api/browse/"</c> — and yields the single form
    /// <see cref="PathString.StartsWithSegments(PathString)"/> matches on.
    /// </summary>
    private static PathString[] NormalizePrefixes(string[]? configured)
    {
        var normalized = new List<PathString>();

        foreach (var raw in configured ?? [])
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            trimmed = trimmed.TrimEnd('/');
            if (trimmed.Length == 0)
                continue; // "/" on its own would mean "meter everything"; treated as not configured.

            if (trimmed[0] != '/')
                trimmed = "/" + trimmed;

            var candidate = new PathString(trimmed);
            if (!normalized.Contains(candidate))
                normalized.Add(candidate);
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException(
                $"{nameof(SparkRateLimiterOptions)}.{nameof(SparkRateLimiterOptions.PathPrefixes)} " +
                "must name at least one path prefix. A rate limiter scoped to no paths meters no " +
                "requests, which would leave the app unprotected with nothing to indicate it. " +
                "Leave the property at its default (\"/spark\", \"/connect\") or list the prefixes " +
                "to meter.",
                nameof(configured));
        }

        return [.. normalized];
    }
}
