using Microsoft.Extensions.Configuration;
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
    {
        var section = builder.Configuration?.GetSection(ConfigurationSection);

        builder.Services.AddSparkMessaging(options =>
        {
            section?.Bind(options);
            configure?.Invoke(options);
        });

        // Register middleware callback to create messaging indexes at startup
        builder.Registry.AddMiddleware(app =>
            SparkMessagingExtensions.CreateSparkMessagingIndexes(app));

        return builder;
    }
}
