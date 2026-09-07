namespace MintPlayer.Spark.SubscriptionWorker;

public static class SparkSubscriptionExtensions
{
    /// <summary>
    /// Registers the Spark subscription worker infrastructure.
    /// </summary>
    /// <remarks>
    /// It takes no configuration callback, because there was nothing to configure. The former
    /// <c>SparkSubscriptionOptions</c> was an empty class: it had once declared
    /// <c>WaitForNonStaleIndexes</c> and <c>NonStaleIndexTimeout</c>, nothing ever read either, and
    /// after those were removed the type survived as a parameter whose only effect was to register
    /// an options object no code consulted. A configuration surface that changes no behaviour is
    /// worse than none, so both are gone. A worker that needs a specific non-stale index should
    /// wait for the query it depends on.
    /// </remarks>
    public static IServiceCollection AddSparkSubscriptions(this IServiceCollection services)
        => services;

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
