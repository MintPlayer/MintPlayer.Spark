using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Messaging;

public static class SparkBuilderMessagingExtensions
{
    /// <summary>
    /// Adds Spark durable messaging infrastructure (message bus, subscription manager, indexes).
    /// </summary>
    public static ISparkBuilder AddMessaging(
        this ISparkBuilder builder,
        Action<SparkMessagingOptions>? configure = null)
    {
        builder.Services.AddSparkMessaging(configure);

        // Register middleware callback to create messaging indexes at startup
        builder.Registry.AddMiddleware(app =>
            SparkMessagingExtensions.CreateSparkMessagingIndexes(app));

        return builder;
    }
}
