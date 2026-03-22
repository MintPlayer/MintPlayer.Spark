namespace MintPlayer.Spark.SubscriptionWorker;

public static class SparkSubscriptionExtensions
{
    /// <summary>
    /// Registers the Spark subscription worker infrastructure.
    /// </summary>
    public static IServiceCollection AddSparkSubscriptions(
        this IServiceCollection services,
        Action<SparkSubscriptionOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        return services;
    }

    /// <summary>
    /// Registers a subscription worker as a hosted service.
    /// </summary>
    public static IServiceCollection AddSubscriptionWorker<TWorker>(this IServiceCollection services)
        where TWorker : class, IHostedService
    {
        services.AddHostedService<TWorker>();
        return services;
    }
}
