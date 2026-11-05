using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Refuses to start a host in which middleware was registered for <see cref="SparkMiddlewareStage.BeforePrerendering"/>
/// but <c>UseSparkPrerendering()</c> never applied that stage (<c>docs/html_minification_PRD.md</c> G3). Without it a
/// module such as <c>AddMinification()</c> in an app without server-side rendering would be a silent no-op.
/// </summary>
/// <remarks>
/// An <see cref="IStartupFilter"/>, because the check has to run after the whole of <c>Program.cs</c> has built the
/// pipeline: its <c>next(app)</c> is the application's own configuration, so every stage that will ever be applied
/// has been by the time it returns. Only this stage is checked. <c>UseSpark()</c> applies the other two
/// unconditionally, and a test host that applies stages by hand must keep working.
/// </remarks>
internal static class SparkPendingStagesCheck
{
    public static IServiceCollection AddSparkPendingStagesCheck(this IServiceCollection services)
    {
        services.AddTransient<IStartupFilter, StartupFilter>();
        return services;
    }

    /// <summary>Throws when <see cref="SparkMiddlewareStage.BeforePrerendering"/> has registrants that never ran.</summary>
    internal static void Run(SparkModuleRegistry registry)
    {
        if (registry.PendingStages.Contains(SparkMiddlewareStage.BeforePrerendering))
            throw new InvalidOperationException(
                "Middleware was registered for the BeforePrerendering stage (by AddMinification(), for example), but " +
                "UseSparkPrerendering() was never called, so it would never run. That stage exists only in a " +
                "server-rendered app: call spa.UseSparkPrerendering() inside UseSpaImproved, or remove the registration.");
    }

    private sealed class StartupFilter(SparkModuleRegistry registry) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            Run(registry);
        };
    }
}
