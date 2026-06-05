using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MintPlayer.Spark.Cron;

/// <summary>Fluent surface for registering cron jobs.</summary>
public interface ISparkCronBuilder
{
    /// <summary>
    /// Registers <typeparamref name="TJob"/> as a scoped service and captures its schedule.
    /// The cron expression is read from <typeparamref name="TJob"/>'s static abstract member —
    /// it is never passed as a parameter.
    /// </summary>
    ISparkCronBuilder AddJob<TJob>() where TJob : class, ISparkCronJob;
}

internal sealed class SparkCronBuilder(IServiceCollection services, SparkCronJobRegistry registry) : ISparkCronBuilder
{
    public ISparkCronBuilder AddJob<TJob>() where TJob : class, ISparkCronJob
    {
        // Resolved per-run from a DI scope, so [Inject] dependencies work — never `new TJob()`.
        services.TryAddScoped<TJob>();

        registry.Add(new CronJobDescriptor(
            JobType: typeof(TJob),
            Name: typeof(TJob).Name,
            CronSchedule: TJob.CronSchedule,             // static abstract, read here
            AllowConcurrentRuns: TJob.AllowConcurrentRuns));

        return this;
    }
}
