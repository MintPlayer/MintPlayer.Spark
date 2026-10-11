using System.Text;
using System.Text.Json.Nodes;

namespace MintPlayer.Spark.Live;

/// <summary>
/// The patches live updates send (D24): computed on the wire form, so two values are equal exactly when they
/// would serialize the same — an array, a reference, a <c>TranslatedString</c> included. The old streaming
/// diff compared boxed CLR values with <c>object.Equals</c>, which compares those by reference.
/// </summary>
/// <remarks>
/// Every diff returns <see langword="null"/> when nothing visible changed: no frame is sent, so a write to a
/// row the viewer cannot see has no observable effect at all (the change-timing oracle stays closed).
/// </remarks>
internal static class LiveDiff
{
    /// <summary>
    /// A query page against the page the client holds: a full <c>result</c> the first time, when the columns
    /// changed, or when the patch would not be smaller; otherwise a <c>patch</c> with the rows to upsert (whole
    /// rows, new or changed), the ids removed, the new order when membership or order changed, and the other
    /// top-level fields that changed (<c>totalItems</c>, <c>skip</c>, <c>disabledActions</c>, …).
    /// </summary>
    public static (string Type, JsonNode Payload)? Query(JsonObject? previous, JsonObject next)
    {
        if (previous is null || !JsonNode.DeepEquals(previous["columns"], next["columns"]))
            return ("result", next.DeepClone());

        var before = Items(previous);
        var after = Items(next);
        var beforeById = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (id, item) in before)
            beforeById[id] = item;
        var afterIds = new HashSet<string>(after.Select(a => a.Id), StringComparer.Ordinal);

        var upsert = new JsonArray();
        foreach (var (id, item) in after)
            if (!beforeById.TryGetValue(id, out var old) || !JsonNode.DeepEquals(old, item))
                upsert.Add(item?.DeepClone());

        var remove = new JsonArray();
        foreach (var (id, _) in before)
            if (!afterIds.Contains(id))
                remove.Add(id);

        var patch = new JsonObject();
        if (upsert.Count > 0)
            patch["upsert"] = upsert;
        if (remove.Count > 0)
            patch["remove"] = remove;

        // The order the client ends up with when it only removes: present when that is not the new order.
        var kept = before.Where(b => afterIds.Contains(b.Id)).Select(b => b.Id);
        if (!kept.SequenceEqual(after.Select(a => a.Id), StringComparer.Ordinal))
            patch["order"] = new JsonArray([.. after.Select(a => (JsonNode?)a.Id)]);

        if (Fields(previous, next, "columns", "items") is { Count: > 0 } set)
            patch["set"] = set;

        if (patch.Count == 0)
            return null;

        // A patch is an optimisation, never a cost: when a page turned over almost completely the full
        // result is no larger and simpler to apply.
        return Bytes(patch) < Bytes(next) ? ("patch", patch) : ("result", next.DeepClone());
    }

    /// <summary>
    /// A persistent object against the one the client holds: a full <c>po</c> the first time or when the set
    /// or order of attributes changed (a right granted or revoked), otherwise a <c>poPatch</c> with the changed
    /// attributes (whole) and the other top-level fields that changed (<c>etag</c>, <c>breadcrumb</c>, …).
    /// </summary>
    public static (string Type, JsonNode Payload)? PersistentObject(JsonObject? previous, JsonObject next)
    {
        if (previous is null)
            return ("po", next.DeepClone());

        var before = Attributes(previous);
        var after = Attributes(next);
        if (!before.Select(b => b.Name).SequenceEqual(after.Select(a => a.Name), StringComparer.Ordinal))
            return ("po", next.DeepClone());

        var changed = new JsonArray();
        for (var i = 0; i < after.Count; i++)
            if (!JsonNode.DeepEquals(before[i].Node, after[i].Node))
                changed.Add(after[i].Node?.DeepClone());

        var patch = new JsonObject();
        if (changed.Count > 0)
            patch["attributes"] = changed;
        if (Fields(previous, next, "attributes") is { Count: > 0 } set)
            patch["set"] = set;

        return patch.Count == 0 ? null : ("poPatch", patch);
    }

    private static List<(string Id, JsonNode? Node)> Items(JsonObject result)
        => result["items"] is JsonArray items
            ? [.. items.Select(item => (item?["id"]?.GetValue<string>() ?? "", item))]
            : [];

    private static List<(string Name, JsonNode? Node)> Attributes(JsonObject po)
        => po["attributes"] is JsonArray attributes
            ? [.. attributes.Select(attribute => (attribute?["name"]?.GetValue<string>() ?? "", attribute))]
            : [];

    /// <summary>The top-level fields that differ, with their new value (null when the field went away).</summary>
    private static JsonObject Fields(JsonObject previous, JsonObject next, params string[] except)
    {
        var set = new JsonObject();
        foreach (var (name, value) in next)
            if (!except.Contains(name) && !JsonNode.DeepEquals(previous[name], value))
                set[name] = value?.DeepClone();
        foreach (var (name, _) in previous)
            if (!except.Contains(name) && !next.ContainsKey(name))
                set[name] = null;
        return set;
    }

    internal static int Bytes(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString());
}
