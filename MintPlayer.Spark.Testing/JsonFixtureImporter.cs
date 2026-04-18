using Newtonsoft.Json.Linq;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Testing;

/// <summary>
/// Seeds an <see cref="IDocumentStore"/> from RavenDB query-result-format JSON files.
/// Expected shape: <c>{ "Results": [ { "@metadata": { "@id": "...", "@collection": "..." }, ... } ] }</c>.
/// Mirrors the fixture format used by CronosCore.RavenDB.UnitTests but with an xUnit-native implementation.
/// </summary>
public static class JsonFixtureImporter
{
    public static async Task ImportAsync(IDocumentStore store, params string[] filePaths)
    {
        if (filePaths.Length == 0) return;

        using var session = store.OpenAsyncSession();

        foreach (var path in filePaths)
        {
            var content = await File.ReadAllTextAsync(path);
            var root = JObject.Parse(content);
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
    }
}
