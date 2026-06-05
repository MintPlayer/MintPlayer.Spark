using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Cron;

public static class SparkCronExtensions
{
    /// <summary>
    /// Registers the Spark cron scheduler and (optionally) cron jobs. Safe to call more than once —
    /// the registry and hosted scheduler are registered a single time; each call may add more jobs.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddSpark(builder.Configuration, spark =>
    /// {
    ///     spark.AddCron(cron => cron.AddJob&lt;NightlyCleanup&gt;());
    ///     // or rely on the generated spark.AddCronJobs(); for auto-discovery
    /// });
    /// </code>
    /// </example>
    public static ISparkBuilder AddCron(this ISparkBuilder builder, Action<ISparkCronBuilder>? configure = null)
    {
        var registry = GetOrAddInfrastructure(builder.Services);
        configure?.Invoke(new SparkCronBuilder(builder.Services, registry));
        return builder;
    }

    private static readonly object infrastructureGate = new();

    private static SparkCronJobRegistry GetOrAddInfrastructure(IServiceCollection services)
    {
        // Reuse the registry instance across multiple AddCron calls so configuration-time
        // registrations and the runtime scheduler share the exact same object. The lock keeps the
        // find-or-create atomic if an app wires services from multiple threads (single-threaded on
        // the normal host path).
        lock (infrastructureGate)
        {
            var existing = services
                .FirstOrDefault(d => d.ServiceType == typeof(SparkCronJobRegistry))?
                .ImplementationInstance as SparkCronJobRegistry;

            if (existing is not null)
                return existing;

            var registry = new SparkCronJobRegistry();
            services.AddSingleton(registry);
            services.AddHostedService<SparkCronScheduler>();
            return registry;
        }
    }
}
