using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Seeds an <see cref="IDocumentStore"/> from RavenDB query-result-format JSON files.
/// Expected shape: <c>{ "Results": [ { "@metadata": { "@id": "...", "@collection": "..." }, ... } ] }</c>.
/// </summary>
/// <remarks>
/// Strings are stored verbatim, dates included: write a <see cref="DateTimeOffset"/> exactly as the
/// client stores it (<c>"2027-03-01T09:00:00.0000000-05:00"</c>) and it lands with that offset.
/// </remarks>
public static class JsonFixtureImporter
{
    /// <summary>
    /// Convenience overload that waits for indexes to settle after import.
    /// Equivalent to <c>ImportAsync(store, waitForIndexing: true, filePaths)</c>.
    /// </summary>
    public static Task ImportAsync(IDocumentStore store, params string[] filePaths)
        => ImportAsync(store, waitForIndexing: true, filePaths);

    /// <summary>
    /// Imports the supplied fixture files into <paramref name="store"/>. When
    /// <paramref name="waitForIndexing"/> is <c>true</c> (default), blocks until the database
    /// reports no stale indexes — so the next query a test runs is guaranteed to see the seed
    /// data even if static indexes are involved. Pass <c>false</c> only when you intentionally
    /// want to assert on stale-read behaviour.
    /// </summary>
    public static async Task ImportAsync(
        IDocumentStore store,
        bool waitForIndexing,
        params string[] filePaths)
    {
        if (filePaths.Length == 0) return;

        using var session = store.OpenAsyncSession();

        foreach (var path in filePaths)
        {
            var content = await File.ReadAllTextAsync(path);

            // DateParseHandling.None: a fixture's strings are stored as written. Newtonsoft's default
            // turns every ISO date into a local DateTime, which moved "09:00-05:00" to "15:00" with no
            // offset on a Brussels machine — the stored value then depended on the machine's zone and
            // on DST, and only "Z" values survived (docs/datetimeoffset_query_sort_filter_PRD.md, F1).
            using var reader = new JsonTextReader(new StringReader(content)) { DateParseHandling = DateParseHandling.None };
            var root = JObject.Load(reader);
            var results = root["Results"] as JArray
                ?? throw new InvalidOperationException($"Fixture '{path}' has no 'Results' array.");

            foreach (var entry in results.OfType<JObject>())
            {
                var metadata = entry["@metadata"] as JObject
                    ?? throw new InvalidOperationException($"Fixture entry in '{path}' has no '@metadata'.");

                var id = metadata.Value<string>("@id")
                    ?? throw new InvalidOperationException($"Fixture entry in '{path}' has no '@id'.");

                var payload = (JObject)entry.DeepClone();
                payload.Remove("@metadata");

                var doc = payload.ToObject<Dictionary<string, object?>>()!;

                await session.StoreAsync(doc, id);

                var docMetadata = session.Advanced.GetMetadataFor(doc);
                foreach (var prop in metadata.Properties())
                {
                    docMetadata[prop.Name] = prop.Value.Type switch
                    {
                        JTokenType.String => prop.Value.Value<string>()!,
                        JTokenType.Integer => prop.Value.Value<long>(),
                        JTokenType.Boolean => prop.Value.Value<bool>(),
                        _ => prop.Value.ToString(),
                    };
                }
            }
        }

        await session.SaveChangesAsync();

        if (waitForIndexing)
            await RavenIndexHelper.WaitForNonStaleAsync(store);
    }
}
