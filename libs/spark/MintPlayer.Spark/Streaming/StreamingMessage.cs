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

    [JsonPropertyName("data")]
    public required PersistentObject[] Data { get; set; }
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

    [JsonPropertyName("attributes")]
    public required Dictionary<string, object?> Attributes { get; set; }
}

internal sealed class ErrorMessage : StreamingMessage
{
    [JsonPropertyName("type")]
    public override string Type => "error";

    [JsonPropertyName("message")]
    public required string Message { get; set; }
}
