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
        => builder.AddMessaging(configure, messaging: null);

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
    /// builder.AddMessaging(messaging: messaging => messaging
    ///     .AddLane(lanes => lanes.Queue&lt;ParseSessionMessage&gt;()
    ///         .Ordered()
    ///         .PartitionBy&lt;ParseSessionMessage&gt;(m => m.BuildId)
    ///         .PartitionBy&lt;FinalizeBuildMessage&gt;(m => m.BuildId)
    ///         .MaxPartitionsInFlight(2))
    ///     // A lane configured from the container, which the old eager design could not express:
    ///     .AddLane((lanes, services) =>
    ///     {
    ///         var options = services.GetRequiredService&lt;IOptions&lt;MailOptions&gt;&gt;().Value;
    ///         lanes.Queue("spark-email")
    ///              .Concurrent(options.Workers)
    ///              .Retry(RetrySchedule.Ladder(options.RetryLadder));
    ///     }));
    /// </code>
    /// </example>
    /// </remarks>
    public static ISparkBuilder AddMessaging(
        this ISparkBuilder builder,
        Action<SparkMessagingOptions>? configure,
        Action<ISparkMessagingBuilder>? messaging)
    {
        var section = builder.Configuration?.GetSection(ConfigurationSection);

        builder.Services.AddSparkMessaging(options =>
        {
            section?.Bind(options);
            configure?.Invoke(options);
        }, messaging);

        // Register middleware callback to create messaging indexes at startup
        builder.Registry.AddMiddleware(app =>
            SparkMessagingExtensions.CreateSparkMessagingIndexes(app));

        return builder;
    }
}
