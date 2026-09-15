using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.ClientOperations;

namespace MintPlayer.Spark.Client;

/// <summary>
/// Reads the <c>operations</c> array off a Spark envelope, and applies the one operation a headless
/// client can meaningfully apply: <c>refreshAttribute</c>.
/// </summary>
/// <remarks>
/// <para>
/// Parsing is by hand rather than by <c>Deserialize&lt;ClientOperation[]&gt;</c>, and tolerant at
/// every field — see the remarks on <see cref="SparkClientOperation"/> for why binding to the
/// server's own DTOs would make an unknown operation type fail the entire response.
/// </para>
/// <para>
/// Applying is <b>explicit and caller-driven</b>. Nothing is applied automatically when a response
/// is read, because this SDK keeps no registry of open objects the way the frontend does — the
/// caller holds the <see cref="PersistentObject"/> and decides when a patch lands on it.
/// </para>
/// </remarks>
public static class SparkClientOperations
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses the <c>operations</c> array out of an envelope body. Returns an empty list when the
    /// body is not an envelope, carries no operations, or is not JSON at all — a malformed envelope
    /// is never allowed to fail a response that otherwise succeeded.
    /// </summary>
    public static IReadOnlyList<SparkClientOperation> Parse(string? envelopeJson)
    {
        if (string.IsNullOrWhiteSpace(envelopeJson)) return [];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(envelopeJson);
        }
        catch (JsonException)
        {
            return [];
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty("operations", out var operations)) return [];
            if (operations.ValueKind != JsonValueKind.Array) return [];

            var parsed = new List<SparkClientOperation>();
            foreach (var op in operations.EnumerateArray())
            {
                var one = ParseOne(op);
                if (one is not null) parsed.Add(one);
            }
            return parsed;
        }
    }

    /// <summary>
    /// Applies every <c>refreshAttribute</c> operation addressed to <paramref name="target"/>, in
    /// emission order. Returns the number applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An operation for a different object is skipped. The match is on <b>type and id together</b>:
    /// the same id can exist under two object types, so matching on id alone patches the wrong
    /// object in exactly the case that is hardest to notice.
    /// </para>
    /// <para>
    /// An operation naming an attribute this object does not have is dropped silently, matching the
    /// frontend. ⚠️ That is also why the lookup here is a <c>FirstOrDefault</c> and not the
    /// <see cref="PersistentObject"/> indexer, which throws.
    /// </para>
    /// </remarks>
    public static int Apply(PersistentObject target, IEnumerable<SparkClientOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(operations);

        var applied = 0;
        foreach (var operation in operations.OfType<SparkRefreshAttributeOperation>())
            if (ApplyOne(target, operation)) applied++;
        return applied;
    }

    private static bool ApplyOne(PersistentObject target, SparkRefreshAttributeOperation operation)
    {
        if (operation.AttributeName is null) return false;
        if (!string.Equals(operation.Id, target.Id, StringComparison.Ordinal)) return false;
        if (!string.Equals(operation.ObjectTypeId, target.ObjectTypeId.ToString(), StringComparison.OrdinalIgnoreCase)) return false;

        var attribute = target.Attributes.FirstOrDefault(a => a.Name == operation.AttributeName);
        if (attribute is null) return false;

        var applied = false;

        if (operation.HasValue)
        {
            attribute.Value = operation.Value;
            applied = true;
        }

        // ⚠️ The nested payloads are applied ONLY to an AsDetail attribute. The server writes
        // object/objects as null on every scalar patch, so applying them unconditionally would
        // "change" every scalar attribute on every patch — which on the frontend marks the object
        // dirty and, here, would overwrite a value the patch never mentioned.
        if (attribute is PersistentObjectAttributeAsDetail detail)
        {
            if (operation.HasObject)
            {
                detail.Object = operation.Object;
                applied = true;
            }

            // An empty array is "the grid is now empty", not "no instruction" — hence HasObjects
            // rather than a null check on the list.
            if (operation.HasObjects)
            {
                detail.Objects = operation.Objects;
                applied = true;
            }
        }

        return applied;
    }

    private static SparkClientOperation? ParseOne(JsonElement op)
    {
        if (op.ValueKind != JsonValueKind.Object) return null;

        var type = op.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString() ?? ""
            : "";
        if (type.Length == 0) return null;

        // A clone, because the JsonDocument this element belongs to is disposed when Parse returns.
        var raw = op.Clone();

        return type switch
        {
            "notify" => new SparkNotifyOperation
            {
                Type = type,
                Raw = raw,
                Message = String(op, "message") ?? "",
                Kind = op.TryGetProperty("kind", out var kind) && kind.TryGetInt32(out var kindValue)
                    ? (NotificationKind)kindValue
                    : NotificationKind.Info,
                DurationMs = op.TryGetProperty("durationMs", out var duration) && duration.TryGetInt32(out var durationValue)
                    ? durationValue
                    : null,
            },
            "refreshAttribute" => ParseRefreshAttribute(op, raw, type),
            "refreshQuery" => new SparkRefreshQueryOperation { Type = type, Raw = raw, QueryId = String(op, "queryId") },
            "navigate" => new SparkNavigateOperation
            {
                Type = type,
                Raw = raw,
                ObjectTypeId = String(op, "objectTypeId"),
                Id = String(op, "id"),
                RouteName = String(op, "routeName"),
            },
            "disableAction" => new SparkDisableActionOperation
            {
                Type = type,
                Raw = raw,
                ActionName = String(op, "actionName"),
                TargetKind = op.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object
                    ? String(target, "kind")
                    : null,
            },
            "retry" => new SparkRetryOperation
            {
                Type = type,
                Raw = raw,
                Prompt = new RetryActionPayload
                {
                    Step = op.TryGetProperty("step", out var step) && step.TryGetInt32(out var stepValue) ? stepValue : 0,
                    Title = String(op, "title") ?? "",
                    Message = String(op, "message"),
                    Options = op.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                        ? [.. options.EnumerateArray().Select(e => e.GetString() ?? "")]
                        : [],
                    DefaultOption = String(op, "defaultOption"),
                    PersistentObject = op.TryGetProperty("persistentObject", out var po) && po.ValueKind == JsonValueKind.Object
                        ? Deserialize<PersistentObject>(po)
                        : null,
                },
            },
            _ => new SparkUnknownOperation { Type = type, Raw = raw },
        };
    }

    private static SparkRefreshAttributeOperation ParseRefreshAttribute(JsonElement op, JsonElement raw, string type)
    {
        var hasValue = op.TryGetProperty("value", out var value);
        var hasObject = op.TryGetProperty("object", out var single);
        var hasObjects = op.TryGetProperty("objects", out var many);

        return new SparkRefreshAttributeOperation
        {
            Type = type,
            Raw = raw,
            ObjectTypeId = String(op, "objectTypeId"),
            Id = String(op, "id"),
            AttributeName = String(op, "attributeName"),
            HasValue = hasValue,
            Value = hasValue && value.ValueKind != JsonValueKind.Null ? value.Clone() : null,
            HasObject = hasObject,
            Object = hasObject && single.ValueKind == JsonValueKind.Object
                ? Deserialize<PersistentObject>(single)
                : null,
            HasObjects = hasObjects && many.ValueKind is JsonValueKind.Array or JsonValueKind.Null,
            Objects = hasObjects && many.ValueKind == JsonValueKind.Array
                ? [.. many.EnumerateArray().Select(Deserialize<PersistentObject>).OfType<PersistentObject>()]
                : null,
        };
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Tolerant deserialize: a nested payload this client cannot read is dropped, not thrown on.
    /// The operations contract is forward-compatible by design, and a nested PO is the most likely
    /// place for a newer server to carry something unfamiliar.
    /// </summary>
    private static T? Deserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
