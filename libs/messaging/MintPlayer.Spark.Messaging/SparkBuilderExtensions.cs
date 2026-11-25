using Microsoft.Extensions.Configuration;
using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Messaging;

public static class SparkBuilderMessagingExtensions
{
    private const string ConfigurationSection = "Spark:Messaging";

    /// <summary>
    /// Adds Spark durable messaging infrastructure (message bus, subscription manager, retry sweeper).
    /// <para>
    /// Options bind from the <c>Spark:Messaging</c> configuration section first, then
    /// <paramref name="configure"/> runs — so code wins over configuration, matching
    /// <c>AddReplication</c>. Retry policy was previously reachable only from a C# delegate baked into
    /// <c>Program.cs</c>, which meant an operator could not tune a durable bus's attempts or backoff
    /// per environment without a redeploy.
    /// </para>
    /// <para>
    /// <b>Except per-queue settings</b> (<see cref="SparkMessagingOptions.Queues"/>): there
    /// <c>Spark:Messaging:Queues</c> is applied again <i>after</i> every <c>Configure</c> — this
    /// delegate and any library's — so a queue's thresholds declared in code are defaults that
    /// appsettings, environment variables and user secrets override property by property.
    /// </para>
    /// </summary>
    [global::MintPlayer.Spark.Abstractions.Storage.SparkRequiresStore("Spark.Messaging", global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.Subscriptions)]
    public static ISparkBuilder AddMessaging(
        this ISparkBuilder builder,
        Action<SparkMessagingOptions>? configure = null)
    {
        // #536 D39/D40: delivery runs on the store's neutral subscriptions, so any provider with Subscriptions serves it.
        global::MintPlayer.Spark.Abstractions.Storage.SparkStoreRequirementExtensions.RequireStore(builder, "Spark.Messaging", global::MintPlayer.Spark.Abstractions.Storage.SparkStoreCapabilities.Subscriptions);

        var section = builder.Configuration?.GetSection(ConfigurationSection);

        builder.Services.AddSparkMessaging(options =>
        {
            section?.Bind(options);
            configure?.Invoke(options);
        });

        if (section is not null)
            builder.Services.PostConfigure<SparkMessagingOptions>(options => ApplyQueueConfiguration(section, options));

        // Terminal messages and their progress sidecars carry an expiry (retention). UseSpark() enables
        // expiration once, at the Community licence's 36 h floor (SparkExpiration.MinimumDeleteFrequency):
        // a terminal message can linger that long, which nothing depends on.
        builder.Registry.RequireDocumentExpiration("Messaging");

        return builder;
    }

    /// <summary>
    /// Layers <c>Spark:Messaging:Queues:{name}</c> over whatever code declared for each queue.
    /// Scalars replace; a configured <c>Backoff</c> replaces the code-declared schedule instead of
    /// being appended to it (the binder appends to a non-empty array — the F14 trap).
    /// </summary>
    internal static void ApplyQueueConfiguration(IConfigurationSection messagingSection, SparkMessagingOptions options)
    {
        foreach (var queueSection in messagingSection.GetSection(nameof(SparkMessagingOptions.Queues)).GetChildren())
        {
            if (!options.Queues.TryGetValue(queueSection.Key, out var queue))
                options.Queues[queueSection.Key] = queue = new SparkQueueOptions();

            if (queueSection.GetSection(nameof(SparkQueueOptions.Backoff)).GetChildren().Any())
                queue.Backoff = [];

            queueSection.Bind(queue);
        }
    }
}
