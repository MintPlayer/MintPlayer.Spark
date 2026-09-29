using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// One entry of <see cref="EntityTypeDefinition.Queries"/>: a query rendered as a sub-query tab on
/// the detail page, with the per-entry overrides the parent type may state (#460, D17, D19).
/// </summary>
/// <remarks>
/// <para>
/// Written in the model file either as a bare alias — <c>"company-cars"</c>, the shape every model
/// file had before #460 M15, still accepted and still what the synchronizer writes back when the
/// entry carries no override — or as an object:
/// <code>{ "query": "question-answers", "selectionMode": "multiple", "parentReference": "QuestionId" }</code>
/// </para>
/// <para>
/// ⚠️ Assigning a <see cref="string"/> converts implicitly, so <c>Queries = ["a", "b"]</c> keeps
/// compiling. Comparing does not: match on <see cref="Query"/>, never on the entry.
/// </para>
/// </remarks>
[JsonConverter(typeof(SparkSubQueryJsonConverter))]
public sealed class SparkSubQuery
{
    /// <summary>The query's alias or id.</summary>
    public required string Query { get; set; }

    /// <summary>
    /// Overrides the query's own <see cref="SparkQuery.SelectionMode"/> for this sub-query only;
    /// null inherits it.
    /// </summary>
    public SparkSelectionMode? SelectionMode { get; set; }

    /// <summary>
    /// The attribute of the query's row type that points at this parent, for the parent-reference
    /// auto-fill of a New started from this sub-query (#460, D19). Needed only when the row type has
    /// more than one reference to the parent's type; with exactly one, the framework finds it. A name
    /// that does not exist, or does not reference the parent's type, fails at startup. Overrides
    /// <see cref="SparkQuery.ParentReference"/>.
    /// </summary>
    public string? ParentReference { get; set; }

    /// <summary>Whether the entry carries nothing but the query — the shape written as a bare string.</summary>
    [JsonIgnore]
    public bool IsBare => SelectionMode is null && string.IsNullOrEmpty(ParentReference);

    public static implicit operator SparkSubQuery(string query) => new() { Query = query };

    public override string ToString() => Query;
}

/// <summary>Reads a sub-query entry from a bare alias or an object; writes the bare alias when it can.</summary>
public sealed class SparkSubQueryJsonConverter : JsonConverter<SparkSubQuery>
{
    public override SparkSubQuery? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return new SparkSubQuery { Query = reader.GetString() ?? string.Empty };
            case JsonTokenType.StartObject:
                break;
            default:
                throw new JsonException("A sub-query entry is a query alias or an object with a 'query' field.");
        }

        string? query = null;
        SparkSelectionMode? selectionMode = null;
        string? parentReference = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (string.IsNullOrWhiteSpace(query))
                    throw new JsonException("A sub-query entry object needs a 'query' (the alias or id of the query).");
                return new SparkSubQuery { Query = query, SelectionMode = selectionMode, ParentReference = parentReference };
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Malformed sub-query entry.");

            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "query", StringComparison.OrdinalIgnoreCase))
                query = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            else if (string.Equals(name, "selectionMode", StringComparison.OrdinalIgnoreCase))
                selectionMode = reader.TokenType == JsonTokenType.Null
                    ? null
                    : JsonSerializer.Deserialize<SparkSelectionMode>(ref reader, options);
            else if (string.Equals(name, "parentReference", StringComparison.OrdinalIgnoreCase))
                parentReference = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            else
                reader.Skip();
        }

        throw new JsonException("Unterminated sub-query entry.");
    }

    public override void Write(Utf8JsonWriter writer, SparkSubQuery value, JsonSerializerOptions options)
    {
        if (value.IsBare)
        {
            writer.WriteStringValue(value.Query);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("query", value.Query);
        if (value.SelectionMode is { } mode)
        {
            writer.WritePropertyName("selectionMode");
            JsonSerializer.Serialize(writer, mode, options);
        }
        if (!string.IsNullOrEmpty(value.ParentReference))
            writer.WriteString("parentReference", value.ParentReference);
        writer.WriteEndObject();
    }
}
