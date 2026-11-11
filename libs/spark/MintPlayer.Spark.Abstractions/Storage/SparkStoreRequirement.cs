using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// What a feature library needs from the application's store (#551 D13): capabilities, and optionally one provider.
/// <c>UseSpark()</c> checks every requirement against the registered <see cref="ISparkStore"/> and refuses to start on
/// a mismatch, naming the feature and what is missing. <c>NotSupportedException</c> inside a provider stays the last
/// line of defence.
/// </summary>
/// <param name="Feature">The feature, as the message names it ("Spark.Messaging").</param>
/// <param name="Capabilities">The capabilities it uses.</param>
/// <param name="Provider">
/// The one provider it runs on, as <see cref="ISparkStore.ProviderName"/> spells it, or <see langword="null"/> for any
/// capable provider. A library that injects a provider's own client (RavenDB's <c>IDocumentStore</c>) names it.
/// </param>
public sealed record SparkStoreRequirement(string Feature, SparkStoreCapabilities Capabilities, string? Provider = null);

/// <summary>Well-known <see cref="ISparkStore.ProviderName"/> values.</summary>
public static class SparkStoreProviders
{
    public const string RavenDb = "RavenDB";
    public const string InMemory = "InMemory";
}

public static class SparkStoreRequirementExtensions
{
    /// <summary>Declares that <paramref name="feature"/> needs <paramref name="capabilities"/>, and optionally one provider.</summary>
    public static ISparkBuilder RequireStore(
        this ISparkBuilder builder, string feature, SparkStoreCapabilities capabilities = SparkStoreCapabilities.None, string? provider = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        builder.Services.AddSingleton(new SparkStoreRequirement(feature, capabilities, provider));
        return builder;
    }

    /// <summary>
    /// The requirements no <paramref name="store"/> meets, as messages. <paramref name="store"/> is
    /// <see langword="null"/> when the application registered none.
    /// </summary>
    public static IReadOnlyList<string> Unmet(IEnumerable<SparkStoreRequirement> requirements, ISparkStore? store)
    {
        var messages = new List<string>();
        foreach (var requirement in requirements.DistinctBy(r => (r.Feature, r.Capabilities, r.Provider)))
        {
            if (store is null)
            {
                messages.Add($"{requirement.Feature} needs a store, and none is registered: call "
                    + $"spark.{(requirement.Provider is null or SparkStoreProviders.RavenDb ? "UseRavenDb" : "Use" + requirement.Provider)}() in AddSpark.");
                continue;
            }

            if (requirement.Provider is { } provider && !string.Equals(provider, store.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"{requirement.Feature} runs on {provider} only; this application uses the {store.ProviderName} provider.");
                continue;
            }

            var missing = requirement.Capabilities & ~store.Capabilities;
            if (missing != SparkStoreCapabilities.None)
                messages.Add($"{requirement.Feature} needs {missing}; the {store.ProviderName} provider doesn't offer it.");
        }
        return messages;
    }
}
