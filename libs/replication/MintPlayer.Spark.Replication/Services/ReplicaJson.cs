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

    /// <summary>
    /// Loads <paramref name="id"/> as <paramref name="type"/>. A typed load, not <c>LoadAsync&lt;object&gt;</c>: a document
    /// an ETL task wrote carries no CLR-type metadata, so an untyped load would not materialize the replica type.
    /// </summary>
    public static async Task<object?> LoadAsync(Raven.Client.Documents.Session.IAsyncDocumentSession session, Type type, string id, CancellationToken cancellationToken)
    {
        var load = LoadTypedMethod.MakeGenericMethod(type);
        var task = (Task)load.Invoke(null, [session, id, cancellationToken])!;
        await task;
        return task.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(task);
    }

    private static readonly System.Reflection.MethodInfo LoadTypedMethod =
        typeof(ReplicaJson).GetMethod(nameof(LoadTypedAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static async Task<object?> LoadTypedAsync<T>(Raven.Client.Documents.Session.IAsyncDocumentSession session, string id, CancellationToken cancellationToken)
        => await session.LoadAsync<T>(id, cancellationToken);

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
