using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Messaging;

public static class SparkBuilderMessagingExtensions
{
    private const string ConfigurationSection = "Spark:Messaging";

    /// <summary>
    /// Adds Spark durable messaging infrastructure (message bus, subscription manager, indexes).
    /// <para>
    /// Options bind from the <c>Spark:Messaging</c> configuration section first, then
    /// <paramref name="configure"/> runs — so code wins over configuration, matching
    /// <c>AddReplication</c>. Retry policy was previously reachable only from a C# delegate baked into
    /// <c>Program.cs</c>, which meant an operator could not tune a durable bus's attempts or backoff
    /// per environment without a redeploy.
    /// </para>
    /// </summary>
    public static ISparkBuilder AddMessaging(
        this ISparkBuilder builder,
        Action<SparkMessagingOptions>? configure = null)
        => builder.AddMessaging(configure, lanes: null);

    /// <summary>
    /// Adds messaging and declares how individual lanes behave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A lane is an isolation unit: lanes never block one another. Within an <c>Ordered</c> lane,
    /// messages sharing a partition key run one at a time in broadcast order, and a failing head
    /// blocks only its own partition.
    /// </para>
    /// <example>
    /// <code>
    /// builder.AddMessaging(lanes: lanes =>
    /// {
    ///     lanes.Queue&lt;ParseSessionMessage&gt;()
    ///          .PartitionBy&lt;ParseSessionMessage&gt;(m => m.BuildId)
    ///          .PartitionBy&lt;FinalizeBuildMessage&gt;(m => m.BuildId)
    ///          .Ordered()
    ///          .MaxPartitionsInFlight(2);
    ///
    ///     lanes.Queue("spark-email")
    ///          .Concurrent(maxConcurrency: 8)
    ///          .Retry(RetrySchedule.Ladder("1m 5m 1h 6h 1d 3d 7d"));
    /// });
    /// </code>
    /// </example>
    /// </remarks>
    public static ISparkBuilder AddMessaging(
        this ISparkBuilder builder,
        Action<SparkMessagingOptions>? configure,
        Action<IMessagingLaneBuilder>? lanes)
    {
        var section = builder.Configuration?.GetSection(ConfigurationSection);

        builder.Services.AddSparkMessaging(options =>
        {
            section?.Bind(options);
            configure?.Invoke(options);
        }, lanes);

        // Register middleware callback to create messaging indexes at startup
        builder.Registry.AddMiddleware(app =>
            SparkMessagingExtensions.CreateSparkMessagingIndexes(app));

        return builder;
    }
}
