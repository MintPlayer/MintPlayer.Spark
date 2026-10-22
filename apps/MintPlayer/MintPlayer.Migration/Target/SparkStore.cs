using MintPlayer.Spark;
using Newtonsoft.Json;
using Raven.Client.Documents;
using Raven.Client.Json.Serialization.NewtonsoftJson;

namespace MintPlayer.Migration.Target;

/// <summary>
/// The store and serializer the migration writes and compares with: Spark's own conventions
/// (<see cref="SparkStoreConfiguration.ApplySparkConventions"/>), so colours and ids are stored exactly as the app reads them.
/// </summary>
public static class SparkStore
{
    public static DocumentStore Create(string url, string database)
    {
        var store = new DocumentStore { Urls = [url], Database = database };
        store.ApplySparkConventions();
        store.Initialize();
        return store;
    }

    /// <summary>Plain Newtonsoft serializer customised the way Spark customises the store's — used for the dry-run
    /// JSONL and for the reconciler's round-trip comparison (both sides go through the same serializer).</summary>
    public static JsonSerializer CreateComparisonSerializer()
    {
        var serializer = JsonSerializer.CreateDefault(new JsonSerializerSettings
        {
            DateParseHandling = DateParseHandling.None,
            NullValueHandling = NullValueHandling.Include,
        });
        using var conventions = new DocumentStore();
        conventions.ApplySparkConventions();
        ((NewtonsoftJsonSerializationConventions)conventions.Conventions.Serialization).CustomizeJsonSerializer?.Invoke(serializer);
        return serializer;
    }
}
