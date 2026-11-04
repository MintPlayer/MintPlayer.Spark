using MintPlayer.Spark.Abstractions.Search;

namespace MintPlayer.Spark.Search;

/// <summary>Options of <see cref="SparkSearchExtensions.AddSearch"/>.</summary>
public sealed class SparkSearchOptions
{
    /// <summary>
    /// Whether searches tolerate typos (<c>docs/spark_search_PRD.md</c> D7). A type's <c>search.fuzzy</c> overrides it.
    /// Default on.
    /// </summary>
    public bool Fuzzy { get; set; } = true;

    /// <summary>The most suggestions one request returns. Default 8.</summary>
    public int MaxSuggestions { get; set; } = 8;

    /// <summary>The most results one search returns. Default 50.</summary>
    public int MaxResults { get; set; } = 50;

    /// <summary>A shorter term returns nothing, so a single keystroke never scans the index. Default 2.</summary>
    public int MinTermLength { get; set; } = 2;

    /// <summary>
    /// Requests per client IP per <see cref="RateLimitWindow"/> to the search endpoints, which are anonymous and fire per
    /// keystroke. Default 120.
    /// </summary>
    public int RateLimit { get; set; } = 120;

    /// <summary>The window of <see cref="RateLimit"/>. Default one minute.</summary>
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The SPA's results page, where a search that is not one item lands. Default <c>/search</c>.</summary>
    public string SearchPagePath { get; set; } = "/search";

    /// <summary>
    /// The provider used instead of the RavenDB one, set by an engine package (e.g. <c>s.UseOpenSearch(...)</c>). The
    /// RavenDB provider stays registered as the fallback while that engine is unreachable (D6).
    /// </summary>
    internal Type? EngineProviderType { get; set; }

    /// <summary>The engine package's own services (client, sync worker), registered by <c>AddSearch</c>.</summary>
    internal Action<IServiceCollection>? EngineServices { get; set; }

    /// <summary>
    /// Registers an external engine as the primary provider, with the services it needs. Called by engine packages;
    /// the provider is registered as a singleton unless <paramref name="services"/> registers it.
    /// </summary>
    public SparkSearchOptions UseProvider<TProvider>(Action<IServiceCollection>? services = null) where TProvider : class, ISparkSearchProvider
    {
        EngineProviderType = typeof(TProvider);
        EngineServices = services;
        return this;
    }
}
