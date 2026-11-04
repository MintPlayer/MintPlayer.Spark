using Microsoft.Extensions.DependencyInjection.Extensions;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Abstractions.Search;

namespace MintPlayer.Spark.Search;

/// <summary>
/// <c>spark.AddSearch()</c> (<c>docs/spark_search_PRD.md</c>, #238): search across every entity type whose model has
/// <c>"search": { "enabled": true }</c>, served at <c>/spark/search</c>, <c>/spark/search/suggest</c> and
/// <c>/spark/search/resolve</c> and mapped by <c>MapSpark()</c> with the other modules' endpoints. The app's generated
/// <c>Spark_Search</c> index answers, unless an engine package (<c>s.UseOpenSearch(...)</c>) takes over.
/// </summary>
public static class SparkSearchExtensions
{
    public static ISparkBuilder AddSearch(this ISparkBuilder builder, Action<SparkSearchOptions>? configure = null)
    {
        var options = new SparkSearchOptions();
        configure?.Invoke(options);

        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SearchCircuit>();
        builder.Services.AddSingleton<SearchRateLimiter>();
        builder.Services.AddSingleton<RavenSearchProvider>();
        if (options.EngineProviderType is { } engine)
        {
            options.EngineServices?.Invoke(builder.Services);
            builder.Services.TryAdd(ServiceDescriptor.Singleton(engine, engine));
        }
        builder.Services.AddScoped<ISparkSearchProvider, SearchProviderSelector>();
        builder.Services.AddScoped<SearchReadPath>();
        builder.Services.AddScoped<ISparkSearch, SparkSearchService>();

        builder.Registry.AddEndpoints(endpoints => endpoints.MapSparkSearchEndpoints());
        return builder;
    }
}
