using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raven.Client.Documents;
using Sparrow.Json;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Contract records on the wire, in the stores' own serialization (warehouse WX1). Both sides convert through
/// their RavenDB conventions — Spark's converters for <c>Color</c>, <c>Money</c>, <c>TranslatedString</c> and the
/// rest included — so a record reads back exactly as the owner would have stored it, and the same JSON works for
/// the HTTP push and for what an ETL transform would load.
/// </summary>
internal static class ReplicaJson
{
    /// <summary><paramref name="value"/> as JSON, the way <paramref name="store"/> would store it, without <c>@metadata</c>.</summary>
    public static JsonElement Serialize(IDocumentStore store, object value)
    {
        using var context = JsonOperationContext.ShortTermSingleUse();
        using var blittable = store.Conventions.Serialization.DefaultConverter.ToBlittable(value, context);
        var node = JsonNode.Parse(blittable.ToString())!.AsObject();
        node.Remove("@metadata");
        node.Remove("Id");
        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>Reads <paramref name="payload"/> into <paramref name="type"/> through <paramref name="store"/>'s conventions.</summary>
    /// <exception cref="InvalidOperationException">The payload does not fit the type.</exception>
    public static async Task<object> DeserializeAsync(IDocumentStore store, Type type, JsonElement payload, string id, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The payload is not a JSON object.");

        using var context = JsonOperationContext.ShortTermSingleUse();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload.GetRawText()));
        using var blittable = await context.ReadForMemoryAsync(stream, "replica", cancellationToken);
        try
        {
            return store.Conventions.Serialization.DefaultConverter.FromBlittable(type, blittable, id)
                ?? throw new InvalidOperationException("The payload deserialized to nothing.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            throw new InvalidOperationException($"The payload does not fit {type.Name}: {ex.Message}", ex);
        }
    }
}
