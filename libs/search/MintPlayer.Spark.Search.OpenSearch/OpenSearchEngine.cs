using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions.Search;

namespace MintPlayer.Spark.Search.OpenSearch;

/// <summary>
/// The engine's REST API, as much of it as Spark uses: the index (created on first use), bulk writes, deletes, searches
/// and a sync checkpoint. Every request body is a <see cref="JsonNode"/> tree or serialized by
/// <see cref="JsonSerializer"/>: a term or an id is a JSON string value, never spliced into a query.
/// </summary>
/// <remarks>
/// The checkpoint lives in the same index as a document of type <see cref="StateType"/>, which no search matches (every
/// search filters on the searchable types). So wiping the engine's volume, or deleting the index, also forgets the
/// checkpoint and the next sync copies everything again.
/// </remarks>
internal sealed class OpenSearchEngine(IHttpClientFactory httpClients, SparkOpenSearchOptions options)
{
    internal const string HttpClientName = "MintPlayer.Spark.Search.OpenSearch";

    // From the factory per use: the engine is a singleton, and a captured client would never rotate its handler.
    private HttpClient http => httpClients.CreateClient(HttpClientName);

    /// <summary>Bumped when the mapping changes: a stored index with another version is recreated (a full reindex).</summary>
    internal const int MappingVersion = 1;

    internal const string StateType = "__spark_state";
    private const string StateId = "__spark_state";

    private string Index => options.IndexName;

    /// <summary>Creates the index when it is missing or has another <see cref="MappingVersion"/>.</summary>
    public async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        using (var mapping = await http.GetAsync($"{Index}/_mapping", cancellationToken))
        {
            if (mapping.IsSuccessStatusCode)
            {
                var body = await mapping.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
                var version = body?[Index]?["mappings"]?["_meta"]?["sparkMappingVersion"]?.GetValue<int>();
                if (version == MappingVersion)
                    return;
                using var delete = await http.DeleteAsync(Index, cancellationToken);
                await EnsureSuccessAsync(delete, cancellationToken);
            }
            else if (mapping.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(mapping, cancellationToken);
            }
        }

        var definition = new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["analysis"] = new JsonObject
                {
                    ["analyzer"] = new JsonObject
                    {
                        // Built-in filters only, so a vanilla engine works; the mintplayer.com image adds analysis-icu
                        // for future use (docs/spark_search_PRD.md D13).
                        ["spark_text"] = new JsonObject
                        {
                            ["type"] = "custom",
                            ["tokenizer"] = "standard",
                            ["filter"] = new JsonArray("lowercase", "asciifolding"),
                        },
                    },
                },
            },
            ["mappings"] = new JsonObject
            {
                ["_meta"] = new JsonObject { ["sparkMappingVersion"] = MappingVersion },
                ["properties"] = new JsonObject
                {
                    ["type"] = new JsonObject { ["type"] = "keyword" },
                    ["text"] = new JsonObject { ["type"] = "search_as_you_type", ["analyzer"] = "spark_text" },
                    ["since"] = new JsonObject { ["type"] = "date" },
                    ["skip"] = new JsonObject { ["type"] = "integer" },
                },
            },
        };
        using var create = await http.PutAsync(Index, Json(definition), cancellationToken);
        // Another instance may have created it a moment ago.
        if (create.StatusCode == HttpStatusCode.BadRequest
            && (await create.Content.ReadAsStringAsync(cancellationToken)).Contains("resource_already_exists_exception", StringComparison.Ordinal))
            return;
        await EnsureSuccessAsync(create, cancellationToken);
    }

    /// <summary>Indexes and deletes in one bulk request.</summary>
    public async Task BulkAsync(IReadOnlyCollection<SparkSearchEntry> upserts, IReadOnlyCollection<string> deletes, CancellationToken cancellationToken)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
            return;

        var ndjson = new StringBuilder();
        foreach (var entry in upserts)
        {
            ndjson.Append(JsonSerializer.Serialize(new { index = new { _index = Index, _id = entry.Id } })).Append('\n');
            ndjson.Append(JsonSerializer.Serialize(new { type = entry.Type, text = entry.Text ?? "" })).Append('\n');
        }
        foreach (var id in deletes)
            ndjson.Append(JsonSerializer.Serialize(new { delete = new { _index = Index, _id = id } })).Append('\n');

        using var content = new StringContent(ndjson.ToString(), Encoding.UTF8, "application/x-ndjson");
        using var response = await http.PostAsync("_bulk", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        if (body?["errors"]?.GetValue<bool>() == true)
        {
            // A delete of a missing document is a 404 item, not a failure.
            var failed = body["items"]?.AsArray()
                .Select(i => i?.AsObject().FirstOrDefault().Value)
                .Where(i => i?["error"] is not null && i["status"]?.GetValue<int>() != 404)
                .ToList();
            if (failed is { Count: > 0 })
                throw new HttpRequestException($"The engine refused {failed.Count} of a bulk request's items: {failed[0]!["error"]}");
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync($"{Index}/_doc/{Uri.EscapeDataString(id)}", cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Drops the index, so the sync rebuilds it from <c>Spark_Search</c>.</summary>
    public async Task DropAsync(CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(Index, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>Runs a query; the hits' ids and types, best first.</summary>
    public async Task<IReadOnlyList<SparkSearchCandidate>> SearchAsync(JsonObject query, int size, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["size"] = size,
            ["_source"] = new JsonArray("type"),
            ["query"] = query,
        };
        using var response = await http.PostAsync($"{Index}/_search", Json(body), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        var hits = result?["hits"]?["hits"]?.AsArray() ?? [];
        return hits
            .Select(h => (Id: h?["_id"]?.GetValue<string>(), Type: h?["_source"]?["type"]?.GetValue<string>()))
            .Where(h => h.Id is not null && h.Type is not null && h.Type != StateType)
            .Select(h => new SparkSearchCandidate(h.Type!, h.Id!))
            .ToList();
    }

    /// <summary>The sync checkpoint, or null when the index has none (new, wiped or recreated).</summary>
    public async Task<SyncCheckpoint?> ReadCheckpointAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"{Index}/_doc/{StateId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        if (body?["_source"] is not JsonObject source || source["since"] is null)
            return null;
        return new SyncCheckpoint(source["since"]!.GetValue<DateTime>(), source["skip"]?.GetValue<int>() ?? 0);
    }

    public async Task WriteCheckpointAsync(SyncCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["type"] = StateType,
            ["since"] = DateTime.SpecifyKind(checkpoint.Since, DateTimeKind.Utc).ToString("O"),
            ["skip"] = checkpoint.Skip,
        };
        using var response = await http.PutAsync($"{Index}/_doc/{StateId}", Json(body), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"The search engine answered {(int)response.StatusCode} to {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: {Truncate(text)}",
            null,
            response.StatusCode);
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}

/// <summary>
/// How far the engine has copied <c>Spark_Search</c>: every row before <see cref="Since"/>, and the first
/// <see cref="Skip"/> rows at it (in id order).
/// </summary>
internal sealed record SyncCheckpoint(DateTime Since, int Skip);
