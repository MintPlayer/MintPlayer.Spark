using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Extensions;

namespace MintPlayer.Spark.Visitor;

/// <summary>Registration and the <c>UseSpark()</c> step of visitor identity (commerce PRD X1).</summary>
internal static class SparkVisitorMiddleware
{
    public static IServiceCollection AddSparkVisitor(this IServiceCollection services)
    {
        services.AddSparkConfigurationSection<SparkVisitorOptions>(SparkVisitorOptions.SectionName);
        services.AddHttpContextAccessor();
        services.AddScoped<SparkVisitorMerge>();
        services.AddScoped<ISparkVisitorMerge>(sp => sp.GetRequiredService<SparkVisitorMerge>());
        // Injected into an endpoint or an action: the request's feature, the same object the middleware made.
        services.AddScoped<ISparkVisitor>(sp => SparkVisitorFeature.For(
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext
            ?? throw new InvalidOperationException("ISparkVisitor is a request service: there is no HTTP request here.")));
        return services;
    }

    /// <summary>
    /// Reads the visitor cookie into the request's <see cref="ISparkVisitor"/> feature, and merges a visitor into
    /// the account of an authenticated request that still carries one (the backstop of the sign-in hook).
    /// </summary>
    /// <remarks>
    /// After <c>UseAuthentication</c>, so <see cref="ISparkVisitor.UserId"/> is known, and before the antiforgery
    /// gate, which holds an anonymous request with a valid visitor cookie to the token check.
    /// </remarks>
    public static IApplicationBuilder UseSparkVisitor(this IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetService<ILoggerFactory>()?.CreateLogger("MintPlayer.Spark.Visitor");
        return app.Use(async (context, next) =>
        {
            var visitor = SparkVisitorFeature.For(context);

            // Only beside a cookie credential: a bearer or API-token caller's subject is not necessarily a local
            // account, and a browser never sends one of those beside its cookies by itself.
            if (visitor.VisitorId is { } visitorId && visitor.UserId is { } userId && visitor.IsAmbientlyAuthenticated)
            {
                try
                {
                    await context.RequestServices.GetRequiredService<SparkVisitorMerge>().MergeAsync(visitorId, userId, context.RequestAborted);
                    visitor.Forget();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The request goes on; the cookie stays, so the next request tries again.
                    logger?.LogError(ex, "Merging visitor {VisitorId} into user {UserId} failed; it is retried on the next request", visitorId, userId);
                }
            }

            await next(context);
        });
    }
}
