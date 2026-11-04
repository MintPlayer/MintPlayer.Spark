using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions.Search;

namespace MintPlayer.Spark.Search.OpenSearch;

/// <summary>
/// The OpenSearch provider (<c>docs/spark_search_PRD.md</c> D6/D7). Every word must match (<c>operator: and</c>); while
/// typing, the last one as a prefix over the <c>search_as_you_type</c> sub-fields; a fuzzy type also matches within
/// <c>fuzziness: AUTO</c>. Like every provider it only proposes candidates: the core re-reads them as the caller.
/// </summary>
internal sealed class OpenSearchSearchProvider(OpenSearchEngine engine) : ISparkSearchProvider
{
    public Task<IReadOnlyList<SparkSearchCandidate>> SearchAsync(SparkSearchRequest request, CancellationToken cancellationToken)
    {
        var words = SearchTerms.Words(request.Term);
        if (words.Count == 0 || request.EntityTypes.Count == 0)
            return Task.FromResult<IReadOnlyList<SparkSearchCandidate>>([]);
        var text = string.Join(' ', words);

        var should = new JsonArray(Match(text, request.Prefix, fuzzy: false));
        if (request.FuzzyTypes.Count > 0)
        {
            should.Add(new JsonObject
            {
                ["bool"] = new JsonObject
                {
                    ["filter"] = new JsonArray(Terms(request.FuzzyTypes)),
                    ["must"] = new JsonArray(Match(text, request.Prefix, fuzzy: true)),
                },
            });
        }

        var query = new JsonObject
        {
            ["bool"] = new JsonObject
            {
                ["filter"] = new JsonArray(Terms(request.EntityTypes)),
                ["should"] = should,
                ["minimum_should_match"] = 1,
            },
        };
        return engine.SearchAsync(query, request.Take, cancellationToken);
    }

    public Task<IReadOnlyList<SparkSearchCandidate>> FindByLabelAsync(string label, IReadOnlyCollection<string> entityTypes, CancellationToken cancellationToken)
    {
        var words = SearchTerms.Words(label);
        if (words.Count == 0 || entityTypes.Count == 0)
            return Task.FromResult<IReadOnlyList<SparkSearchCandidate>>([]);

        var query = new JsonObject
        {
            ["bool"] = new JsonObject
            {
                ["filter"] = new JsonArray(Terms(entityTypes)),
                ["must"] = new JsonArray(Match(string.Join(' ', words), prefix: false, fuzzy: false)),
            },
        };
        return engine.SearchAsync(query, FindByLabelTake, cancellationToken);
    }

    /// <summary>As the RavenDB provider's: beyond this, numbering namesakes is meaningless.</summary>
    internal const int FindByLabelTake = 100;

    private static JsonObject Terms(IEnumerable<string> types)
        => new() { ["terms"] = new JsonObject { ["type"] = new JsonArray(types.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()) } };

    private static JsonObject Match(string text, bool prefix, bool fuzzy)
    {
        JsonObject clause;
        if (prefix)
        {
            clause = new JsonObject
            {
                ["query"] = text,
                ["type"] = "bool_prefix",
                ["fields"] = new JsonArray("text", "text._2gram", "text._3gram"),
                ["operator"] = "and",
            };
            if (fuzzy)
                clause["fuzziness"] = "AUTO";
            return new JsonObject { ["multi_match"] = clause };
        }

        clause = new JsonObject { ["query"] = text, ["operator"] = "and" };
        if (fuzzy)
            clause["fuzziness"] = "AUTO";
        return new JsonObject { ["match"] = new JsonObject { ["text"] = clause } };
    }
}
