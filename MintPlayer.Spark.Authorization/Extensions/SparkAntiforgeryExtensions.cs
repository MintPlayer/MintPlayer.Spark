using Microsoft.AspNetCore.Builder;
using MintPlayer.AspNetCore.SpaServices.Xsrf;

namespace MintPlayer.Spark.Authorization.Extensions;

public static class SparkAntiforgeryExtensions
{
    /// <summary>
    /// Adds XSRF/CSRF protection for Spark cookie authentication.
    /// Generates an XSRF-TOKEN cookie on every response and validates
    /// the X-XSRF-TOKEN header on mutation endpoints.
    /// Call this after <c>UseAuthorization()</c> and before <c>UseSpark()</c>.
    /// </summary>
    public static IApplicationBuilder UseSparkAntiforgery(this IApplicationBuilder app)
    {
        app.UseAntiforgeryGenerator();
        app.UseAntiforgery();
        return app;
    }
}
