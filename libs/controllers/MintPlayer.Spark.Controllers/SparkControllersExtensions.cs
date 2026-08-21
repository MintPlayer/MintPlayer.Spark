using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Controllers;

/// <summary>
/// Mounts an application's own MVC controllers <em>inside</em> Spark's pipeline, so they are subject
/// to the same rules Spark's own endpoints are — authentication schemes, authorization, the
/// antiforgery gate — instead of sitting beside them with only whatever the app wired by hand.
/// <code>
/// builder.Services.AddSpark(spark =>
/// {
///     spark.AddControllers();
///     spark.UseControllers();
/// });
///
/// app.MapSpark();   // controllers are mounted here, with everything else Spark maps
/// </code>
/// <para>
/// <b>Why not just <c>endpoints.MapControllers()</c>.</b> Nothing about a controller mapped that way
/// tells Spark it exists, so nothing Spark configures can be scoped to it — which is how an app ends
/// up with five cookie-authenticated mutating endpoints and no CSRF check on any of them (#300).
/// Mounting through the registry makes the app's controllers part of the surface Spark knows about.
/// A bare <c>MapControllers()</c> in a project that references Spark is reported by analyzer
/// <c>SPARK010</c>, because at compile time the call is plainly visible even though at runtime it
/// leaves no trace.
/// </para>
/// </summary>
public static class SparkControllersExtensions
{
    /// <summary>
    /// Registers MVC's services. Configuration only — nothing is mounted until
    /// <see cref="UseControllers"/>, mirroring ASP.NET Core's own <c>Add</c>/<c>Use</c> split so the
    /// two decisions stay separable (an app can register controllers for a health probe it maps
    /// itself, or configure MVC without exposing anything).
    /// <para>
    /// Safe next to an app's own <c>builder.Services.AddControllers()</c>: MVC's registration is
    /// idempotent, and <paramref name="configure"/> receives the same <see cref="IMvcBuilder"/> the
    /// app already configured, so an earlier <c>.AddJsonOptions(…)</c> survives.
    /// </para>
    /// </summary>
    /// <param name="configure">
    /// Applied to the resulting <see cref="IMvcBuilder"/> — the seam for <c>.AddJsonOptions(…)</c>,
    /// <c>.AddApplicationPart(…)</c> and the rest of MVC's own configuration surface. Passing the
    /// real builder rather than a bespoke options object keeps every MVC feature reachable without
    /// this package having to grow a property per feature.
    /// </param>
    public static ISparkBuilder AddControllers(
        this ISparkBuilder builder,
        Action<IMvcBuilder>? configure = null)
    {
        var mvc = builder.Services.AddControllers();
        configure?.Invoke(mvc);
        return builder;
    }

    /// <summary>
    /// Mounts every discovered controller action when <c>MapSpark()</c> runs.
    /// <para>
    /// Idempotent: a second call is ignored rather than mapping the controller endpoints twice. That
    /// matters during migration, when an app may reasonably call this and still have its old
    /// <c>MapControllers()</c> in place — but it can only guard the calls it can see, which is
    /// exactly why the bare call is reported by <c>SPARK010</c> rather than silently tolerated.
    /// </para>
    /// </summary>
    public static ISparkBuilder UseControllers(this ISparkBuilder builder)
    {
        if (!MarkMounted(builder.Services))
            return builder;

        builder.Registry.AddEndpoints(endpoints => endpoints.MapControllers());
        return builder;
    }

    /// <summary>
    /// Records the mount in the service collection — the one piece of state that outlives an
    /// <c>AddSpark</c> lambda and is reachable from every overload. Returns false when it was
    /// already recorded.
    /// </summary>
    private static bool MarkMounted(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(ControllersMountedMarker)))
            return false;

        services.AddSingleton(new ControllersMountedMarker());
        return true;
    }

    private sealed class ControllersMountedMarker;
}
