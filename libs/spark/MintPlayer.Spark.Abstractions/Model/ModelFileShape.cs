using System.Text;
using System.Text.Json;

namespace MintPlayer.Spark.Abstractions.Model;

/// <summary>
/// Renders the <em>structural</em> content of a model JSON file as canonical text, ignoring
/// everything presentational.
///
/// <para>
/// This is the on-disk counterpart to <see cref="SparkModelShape"/>. The shape hashes say what the
/// entity classes require; these say what the model directory actually contains. Both are needed: a
/// file planted in that directory has no CLR counterpart, so the shape hashes cannot see it — and
/// the model loader reads whatever is in the directory.
/// </para>
///
/// <para>
/// The split between structural and presentational is the whole point. Model JSON is hand-editable
/// by design: labels and their translations, renderers, groups, tabs, ordering and column spans are
/// authored by humans and preserved across synchronization. Hashing raw file bytes would make a
/// translated label stop an application from starting. Hashing only the structural fields keeps
/// tampering detectable while leaving that workflow free.
/// </para>
///
/// <para>
/// <b>Structural</b> (hashed): entity name, CLR type, alias, projection/query type, index name, and
/// per attribute — name, data type, required, read-only, array-ness, reference target, detail type,
/// lookup type, sortability, projection membership, and validation rules. Validation is included
/// deliberately: silently dropping a rule is an attack, not a restyling.
/// </para>
///
/// <para>
/// <b>Presentational</b> (ignored): labels and translations, description, breadcrumb, renderer and
/// renderer options, group, tabs, edit mode, reference display type, visibility, order, column span,
/// and the generated <c>id</c> values.
/// </para>
/// </summary>
public static class ModelFileShape
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Structural hash per model file, keyed by file name. Sharded rather than rolled into one value
    /// so a failure can name the file, and so unrelated files do not collide in a merge.
    /// </summary>
    public static SortedDictionary<string, string> ComputeFileHashes(string modelDirectory)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(modelDirectory))
            return result;

        foreach (var path in Directory.GetFiles(modelDirectory, "*.json"))
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, ModelHashFile.FileName, StringComparison.OrdinalIgnoreCase))
                continue;

            result[name] = Sha256Hex(Describe(path));
        }

        return result;
    }

    /// <summary>
    /// Canonical structural text for one model file. Unparseable files yield a marker rather than
    /// throwing: a corrupt file must still be detectable, and it must not take the process down
    /// before the check can report it.
    /// </summary>
    public static string Describe(string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return "unparseable\n";
        }

        using (document)
        {
            var builder = new StringBuilder();

            if (!document.RootElement.TryGetProperty("persistentObject", out var po))
                return "no-persistent-object\n";

            AppendScalar(builder, "name", po);
            AppendScalar(builder, "clrType", po);
            AppendScalar(builder, "alias", po);
            AppendScalar(builder, "queryType", po);
            AppendScalar(builder, "indexName", po);

            if (po.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Array)
            {
                // Ordinal-sorted by name: attribute order in the file is presentation, and reordering
                // must not read as tampering.
                foreach (var attribute in attributes.EnumerateArray()
                             .OrderBy(a => a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", StringComparer.Ordinal))
                {
                    builder.Append("  attr");
                    foreach (var field in StructuralAttributeFields)
                        AppendInline(builder, field, attribute);

                    AppendRules(builder, attribute);
                    builder.Append('\n');
                }
            }

            return builder.ToString();
        }
    }

    private static readonly string[] StructuralAttributeFields =
    [
        "name", "dataType", "isRequired", "isReadOnly", "isArray",
        "referenceType", "asDetailType", "lookupReferenceType", "isSortable",
        "inCollectionType", "inQueryType", "query",
    ];

    private static void AppendRules(StringBuilder builder, JsonElement attribute)
    {
        if (!attribute.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return;

        // Validation is structural: dropping a rule from a deployed model weakens what the server
        // accepts, which is exactly the kind of edit this is meant to notice.
        var rendered = rules.EnumerateArray()
            .Select(r => r.GetRawText())
            .OrderBy(r => r, StringComparer.Ordinal);

        builder.Append("\trules=[").Append(string.Join(",", rendered)).Append(']');
    }

    private static void AppendScalar(StringBuilder builder, string name, JsonElement parent)
    {
        if (parent.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            builder.Append(name).Append('\t').Append(Render(value)).Append('\n');
    }

    private static void AppendInline(StringBuilder builder, string name, JsonElement parent)
    {
        if (parent.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            builder.Append('\t').Append(name).Append('=').Append(Render(value));
    }

    private static string Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    private static string Sha256Hex(string text)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Utf8NoBom.GetBytes(text)));
}
