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
/// The limiter is partitioned by client IP and scoped to requests under <c>/spark/</c>, so
/// static assets and app-specific routes remain unthrottled. Over-limit requests are rejected
/// with HTTP 429. The rate-limiter middleware registers itself through the Spark builder
/// registry — no separate <c>app.UseRateLimiter()</c> call needed when the app uses
/// <c>UseSpark()</c> / <c>UseSparkFull()</c>.
/// </summary>
public static class SparkBuilderRateLimiterExtensions
{
    /// <summary>
    /// Registers a fixed-window rate limiter for Spark endpoints. Calling with no configurator
    /// uses the documented defaults (<see cref="SparkRateLimiterOptions"/>).
    /// </summary>
    public static ISparkBuilder AddRateLimiter(
        this ISparkBuilder builder,
        Action<SparkRateLimiterOptions>? configure = null)
    {
        var options = new SparkRateLimiterOptions();
        configure?.Invoke(options);

        builder.Services.AddRateLimiter(rl =>
        {
            rl.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                // Only Spark routes are throttled. SPA static files, controllers, and any
                // non-framework endpoints remain unmetered so the limiter never starves
                // browser asset loads.
                if (!httpContext.Request.Path.StartsWithSegments("/spark"))
                    return RateLimitPartition.GetNoLimiter("no-limit");

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
}
