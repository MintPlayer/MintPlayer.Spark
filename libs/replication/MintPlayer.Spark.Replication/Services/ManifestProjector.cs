using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Replication.Abstractions.Rendering;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// The HTTP transport's projection (warehouse §4.2.3): interprets an approved manifest over a source document's raw
/// JSON with the generated script's semantics, so a push carries what the ETL transform would load. Considered and
/// rejected: a JavaScript <c>select</c> projection (the script would be spliced into RQL, and it would run
/// consumer-shaped JavaScript), a generated C# projection on the owner (it does not reference consumer types), and an
/// embedded JS engine (a second engine with subtly different semantics).
/// <para>
/// Semantics of <c>A: this.X</c> / <c>A: (this.X == null ? null : this.X.Y)</c>: a missing last member is
/// <c>undefined</c>, which JSON drops, so the field is omitted; a missing or null intermediate member gives
/// <c>null</c>.
/// </para>
/// </summary>
internal static class ManifestProjector
{
    public static JsonObject Project(ReplicaManifest manifest, JsonElement source)
    {
        var result = new JsonObject();
        foreach (var field in manifest.Fields)
        {
            if (TryRead(source, field.Path, out var value))
                result[field.Target] = value;
        }
        return result;
    }

    /// <summary>The value at <paramref name="path"/>; false when the last member is missing (undefined).</summary>
    private static bool TryRead(JsonElement source, IReadOnlyList<string> path, out JsonNode? value)
    {
        var current = source;
        for (var i = 0; i < path.Count; i++)
        {
            var last = i == path.Count - 1;
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(path[i], out var next))
            {
                // `this.A.B` with A missing: the null-check renders null. A missing last member: undefined, omitted.
                value = null;
                return !last;
            }

            if (!last && next.ValueKind == JsonValueKind.Null)
            {
                value = null;
                return true;
            }

            current = next;
        }

        value = JsonNode.Parse(current.GetRawText());
        return true;
    }
}
