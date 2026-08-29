using MintPlayer.Spark.Abstractions;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Streaming;

[JsonDerivedType(typeof(SnapshotMessage))]
[JsonDerivedType(typeof(PatchMessage))]
[JsonDerivedType(typeof(ErrorMessage))]
internal abstract class StreamingMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

internal sealed class SnapshotMessage : StreamingMessage
{
    [JsonPropertyName("type")]
    public override string Type => "snapshot";

    /// <summary>The column metadata for the whole stream, sent once with the snapshot.</summary>
    [JsonPropertyName("columns")]
    public required IReadOnlyList<QueryColumn> Columns { get; set; }

    [JsonPropertyName("data")]
    public required IReadOnlyList<QueryResultItem> Data { get; set; }
}

internal sealed class PatchMessage : StreamingMessage
{
    [JsonPropertyName("type")]
    public override string Type => "patch";

    [JsonPropertyName("updated")]
    public required PatchItem[] Updated { get; set; }
}

internal sealed class PatchItem
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    /// <summary>Changed cell values, keyed by column name. Never metadata — the snapshot fixed that.</summary>
    [JsonPropertyName("values")]
    public required Dictionary<string, object?> Values { get; set; }
}

internal sealed class ErrorMessage : StreamingMessage
{
    [JsonPropertyName("type")]
    public override string Type => "error";

    [JsonPropertyName("message")]
    public required string Message { get; set; }
}
