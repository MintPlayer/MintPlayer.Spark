using MintPlayer.Spark.Abstractions.Interceptors;

namespace MintPlayer.Spark.Search.OpenSearch;

/// <summary>
/// <c>s.UseOpenSearch(...)</c> inside <c>spark.AddSearch(s =&gt; ...)</c> (<c>docs/spark_search_PRD.md</c> D6): an
/// OpenSearch engine answers searches, kept in sync from the app's <c>Spark_Search</c> index, which keeps answering
/// while the engine is unreachable.
/// </summary>
/// <remarks>Needs Spark Messaging (<c>spark.AddMessaging()</c>): hard deletes reach the engine as durable messages.</remarks>
public static class SparkOpenSearchExtensions
{
    public static SparkSearchOptions UseOpenSearch(this SparkSearchOptions search, Action<SparkOpenSearchOptions> configure)
    {
        var options = new SparkOpenSearchOptions();
        configure(options);
        options.Validate();

        return search.UseProvider<OpenSearchSearchProvider>(services =>
        {
            services.AddSingleton(options);
            services.AddHttpClient(OpenSearchEngine.HttpClientName, client =>
            {
                client.BaseAddress = options.Uri!.AbsoluteUri.EndsWith('/') ? options.Uri : new Uri(options.Uri.AbsoluteUri + "/");
                client.Timeout = options.RequestTimeout;
            });
            services.AddSingleton<OpenSearchEngine>();
            services.AddSingleton<OpenSearchSync>();
            services.AddHostedService(sp => sp.GetRequiredService<OpenSearchSync>());
            services.AddSingleton<ISparkSearchReindex, OpenSearchReindex>();
            services.AddSparkInterceptor<OpenSearchDeleteInterceptor>();
        });
    }
}

/// <summary>Rebuilds the engine index from <c>Spark_Search</c>, e.g. after the search model changed.</summary>
public interface ISparkSearchReindex
{
    /// <summary>Drops the engine index; the sync fills a new one from the start. Searches use RavenDB meanwhile.</summary>
    Task ReindexAsync(CancellationToken cancellationToken = default);
}

internal sealed class OpenSearchReindex(OpenSearchEngine engine, OpenSearchSync sync) : ISparkSearchReindex
{
    public async Task ReindexAsync(CancellationToken cancellationToken = default)
    {
        await engine.DropAsync(cancellationToken);
        await sync.SyncOnceAsync(cancellationToken);
    }
}
