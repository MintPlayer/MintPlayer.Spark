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
/// <b>Structural</b> (hashed): entity name, CLR type, alias, projection/query type, index name, per
/// attribute — name, data type, required, read-only, array-ness, reference
/// target, detail type, lookup type, sortability, projection membership, and validation rules — and
/// per inline query its
/// <c>indexName</c>. Validation is included deliberately: silently dropping a rule is an attack, not
/// a restyling. A query's <c>indexName</c> is structural since issue #279 made it load-bearing: the
/// runtime resolves the index through it, so a hand-edit changes which index answers the query and
/// must trip verification. The rest of a query (name, sort columns, render mode) stays presentational.
/// </para>
///
/// <para>
/// <b>Presentational</b> (ignored): labels and translations, description, breadcrumb, renderer and
/// renderer options, group, tabs, edit mode, reference display type, order, column span, and the
/// generated <c>id</c> values.
/// </para>
///
/// <para>
/// The rule is that every write gate is structural. See <see cref="StructuralAttributeFields"/>.
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
        => ComputeFileHashes([], modelDirectory);

    /// <summary>
    /// Structural hash per type of the composed model (composition D6): <paramref name="libraries"/>'
    /// model layers with the files in <paramref name="modelDirectory"/> on top. Keyed by file name, a
    /// library type by the library's file name, so a type that moves from the application into a
    /// library keeps its key, and its hash when its structure is unchanged (ids are not structural).
    /// </summary>
    /// <remarks>
    /// Each layer's identity is <see cref="ComputeLayerHashes"/>'. A composition the run time would
    /// refuse is hashed as composed here: the startup check reports it in its own words.
    /// </remarks>
    public static SortedDictionary<string, string> ComputeFileHashes(IEnumerable<SparkLibrary> libraries, string modelDirectory)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in SparkModelFiles.ComposeLenient(libraries, modelDirectory).Types)
            result[type.FileName] = Sha256Hex(DescribeJson(type.Json));

        return result;
    }

    /// <summary>
    /// The provenance of every composed type a library states (composition D7), keyed
    /// <c>Model/{file name}</c> like <see cref="ComputeFileHashes(IEnumerable{SparkLibrary}, string)"/>'s
    /// keys: each layer (<see cref="SparkLayerProvenance"/>: a library's alias, <c>app</c> for the
    /// application's delta) and the structural hash of what that layer states. A type only the
    /// application states has no entry: its file hash is its one layer's.
    /// </summary>
    /// <remarks>
    /// Structural for the same reason the file hash is: a library update that only relabels its type
    /// must not stop an application, and one that changes a structure must name itself.
    /// </remarks>
    public static SortedDictionary<string, SortedDictionary<string, string>> ComputeLayerHashes(IEnumerable<SparkLibrary> libraries, string? modelDirectory)
    {
        var list = libraries.ToList();
        var result = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var type in SparkModelFiles.ComposeLenient(list, modelDirectory).Types)
        {
            var inputs = type.Entry.Inputs;
            if (!inputs.Any(i => i.IsLibrary)) continue;

            var layers = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var input in inputs)
                layers[input.IsLibrary ? SparkLayerProvenance.AliasOf(list, input.Layer) : SparkLayerProvenance.App] = Sha256Hex(DescribeJson(input.Json));
            result[LayerKey(type.FileName)] = layers;
        }

        return result;
    }

    /// <summary>A model type's key among every gated entry's layers, apart from the config files'.</summary>
    public static string LayerKey(string fileName) => "Model/" + fileName;

    /// <summary>
    /// Canonical structural text for one model file. Unparseable files yield a marker rather than
    /// throwing: a corrupt file must still be detectable, and it must not take the process down
    /// before the check can report it.
    /// </summary>
    public static string Describe(string path) => DescribeJson(File.ReadAllText(path));

    /// <summary>As <see cref="Describe(string)"/>, for the text of a model file (a composed one, say).</summary>
    public static string DescribeJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return "unparseable\n";
        }

        using (document)
        {
            var builder = new StringBuilder();

            // TryGetProperty THROWS on a non-object, so a file containing `null` (or a
            // `"persistentObject": null`) used to take the verify run down with a stack trace —
            // exactly what the summary above promises it will not do.
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("persistentObject", out var po)
                || po.ValueKind != JsonValueKind.Object)
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

            // Every query contributes a line, and `source` and `entityType` are structural (#327 M3).
            //
            // This used to skip a query entirely unless it carried an `indexName`, which meant
            // "queries": [] and "queries": [<a whole query>] hashed IDENTICALLY — a hand-authored
            // model file could gain a complete query without moving the file hash by one bit. That
            // is exactly the shape composed queries introduce, and the two omitted fields are the
            // two that matter most: `source` names an arbitrary method that runs with no row
            // security, and `entityType` chooses the right that gates the request and selects which
            // actions class is invoked. Editing either on a deployed model must trip the gate.
            if (document.RootElement.TryGetProperty("queries", out var queries) && queries.ValueKind == JsonValueKind.Array)
            {
                foreach (var query in queries.EnumerateArray()
                             .OrderBy(q => q.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", StringComparer.Ordinal))
                {
                    builder.Append("  query");
                    foreach (var field in StructuralQueryFields)
                        AppendInline(builder, field, query);

                    AppendQueryColumns(builder, query);
                    builder.Append('\n');
                }
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// The attribute fields a deployed model may not change without tripping the hash gate.
    /// </summary>
    /// <remarks>
    /// The rule, stated once so the list can be checked against it rather than remembered:
    /// <b>every field that gates a write is structural.</b>
    /// <para>
    /// <c>isReadOnly</c> is the one model-level write gate (<c>EntityMapper.IsWritableBySchema</c>).
    /// ⚠️ <c>isVisible</c> used to be a second one and was hashed for that reason; it was removed
    /// (#264), because a layout flag that also refused writes dropped values an action had
    /// revealed. Layout (<c>showedOn</c>) is presentational and stays off this list.
    /// </para>
    /// <para>
    /// <c>canSort</c>, <c>canFilter</c> and <c>canListDistincts</c> (#431) are here by the same rule.
    /// They read as presentation and are not: the executor refuses a sort, a filter or a distinct
    /// listing on a column whose flag is <c>false</c>, so flipping one to <c>true</c> in a deployed
    /// model opens a read path — which is the sort-oracle class (#294-#296) the gate exists for.
    /// Adding names here churns no existing hash, because a field is only appended when the JSON
    /// property is present and no model file carries these yet.
    /// </para>
    /// </remarks>
    private static readonly string[] StructuralAttributeFields =
    [
        "name", "dataType", "isRequired", "isReadOnly", "isArray",
        "referenceType", "asDetailType", "lookupReferenceType", "isSortable",
        "inCollectionType", "inQueryType", "query",
        "canSort", "canFilter", "canListDistincts",
    ];

    /// <summary>
    /// The query fields a deployed model may not change without tripping the hash gate.
    /// </summary>
    /// <remarks>
    /// <c>source</c> and <c>entityType</c> are the security-relevant pair: the first names the
    /// method that produces the rows, the second names the type whose <c>Query</c> right gates the
    /// request. <c>isStreamingQuery</c> is here because it selects an entirely different execution
    /// path. Presentation — <c>description</c>, <c>renderMode</c>, <c>sortColumns</c> — is not.
    /// </remarks>
    private static readonly string[] StructuralQueryFields =
    [
        "name", "source", "entityType", "indexName", "alias", "isStreamingQuery",
    ];

    /// <summary>
    /// A query's sparse per-column capability overrides (#431), canonicalised into the hash.
    /// </summary>
    /// <remarks>
    /// Structural by the rule this file states: an override gates a read. Flipping
    /// <c>canListDistincts</c> to <c>true</c> on a deployed model opens value enumeration on a column
    /// the author closed, which is the disclosure class the sort-oracle hardening (#294-#296) exists
    /// for — and unlike the attribute-level flags, an override is easy to add to a model file without
    /// touching anything else.
    /// <para>
    /// Canonicalised rather than rendered, for the reason <see cref="AppendRules"/> records:
    /// <c>GetRawText()</c> on an array returns the original bytes including indentation and line
    /// endings, so a CRLF-to-LF rewrite between the machine that writes the file and the container
    /// that verifies it would change the hash and refuse to start.
    /// </para>
    /// <para>
    /// ⚠️ <c>sortColumns</c> is deliberately <b>not</b> structural, though the <c>canSort</c> exemption
    /// (#431 §5.3) gives it a new gating role: a column the query declares its default order by skips
    /// the <c>canSort</c> check. The exposure is bounded and accepted — the exemption cannot reach a
    /// column that is off the query surface, because <c>ShowedOn.Query</c> is checked first and
    /// remains the authorization boundary. Bypassing <c>canSort</c> returns that column to its
    /// pre-#431 behaviour, which was already acceptable. Making <c>sortColumns</c> structural would
    /// churn the hash of nearly every model file to close a hole that is not one.
    /// </para>
    /// </remarks>
    private static void AppendQueryColumns(StringBuilder builder, JsonElement query)
    {
        if (!query.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            return;

        var rendered = columns.EnumerateArray()
            .Select(Canonicalize)
            .OrderBy(c => c, StringComparer.Ordinal);

        builder.Append("\tcolumns=[").Append(string.Join(",", rendered)).Append(']');
    }

    private static void AppendRules(StringBuilder builder, JsonElement attribute)
    {
        if (!attribute.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return;

        // Validation is structural: dropping a rule from a deployed model weakens what the server
        // accepts, which is exactly the kind of edit this is meant to notice.
        //
        // Canonicalised rather than taken as raw text. GetRawText() returns the original bytes,
        // including indentation and line endings — so a CRLF-to-LF rewrite between the machine that
        // writes the file and the container that verifies it would change the hash and stop the
        // application from starting. Found exactly that way.
        var rendered = rules.EnumerateArray()
            .Select(Canonicalize)
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

    /// <summary>
    /// Whitespace-free rendering with object keys ordinally sorted, so only a change of meaning
    /// changes the text. Formatting, key order and line endings are all invisible here.
    /// </summary>
    private static string Canonicalize(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var members = element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => $"\"{p.Name}\":{Canonicalize(p.Value)}");
                return "{" + string.Join(",", members) + "}";

            case JsonValueKind.Array:
                // Array order is meaningful, so it is preserved.
                return "[" + string.Join(",", element.EnumerateArray().Select(Canonicalize)) + "]";

            case JsonValueKind.String:
                return JsonSerializer.Serialize(NormalizeNewlines(element.GetString()));

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return "null";

            default:
                return element.GetRawText();
        }
    }

    private static string Render(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => NormalizeNewlines(value.GetString()) ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    /// <summary>
    /// Collapses CRLF and lone CR to LF in every string that reaches the hash.
    /// <para>
    /// Belt and braces. Parsing the JSON already removes the file's own line endings from the
    /// picture, so this only matters for newlines carried <em>inside</em> a string value. But the
    /// consequence of getting it wrong is an application that refuses to start on Linux after the
    /// hash was written on Windows, and normalising costs nothing.
    /// </para>
    /// </summary>
    private static string? NormalizeNewlines(string? value)
        => value?.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string Sha256Hex(string text)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Utf8NoBom.GetBytes(text)));
}
