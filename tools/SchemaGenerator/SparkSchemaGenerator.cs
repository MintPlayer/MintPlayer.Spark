using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.SchemaGenerator;

/// <summary>One generated schema: <c>{Name}.schema.json</c>, exported from <see cref="Type"/>.</summary>
/// <param name="Name">The schema's name, which is also the last segment of its URL.</param>
/// <param name="Type">
/// The type the server deserializes the file into. Null for <c>translations.json</c>, which no CLR
/// type describes: it is a tree read by the translations source generator, so its schema is written
/// out by hand in <see cref="SparkSchemaGenerator"/>.
/// </param>
/// <param name="Title">The schema's <c>title</c>.</param>
public sealed record SparkSchemaFile(string Name, Type? Type, string Title)
{
    public string FileName => $"{Name}.schema.json";
}

/// <summary>
/// Generates the JSON schemas of Spark's six hand-edited <c>App_Data</c> files (#264, G-Q11..Q13)
/// with <see cref="JsonSchemaExporter"/>, from the types the loaders deserialize.
/// </summary>
/// <remarks>
/// <para>
/// Strict (G-Q12): every object closes with <c>additionalProperties: false</c>, so a typo or a
/// removed property such as <c>isVisible</c> is flagged in the editor, and opens
/// <c>patternProperties: { "^_": {} }</c>, so underscore properties stay available as comments. The
/// root also accepts <c>$schema</c> itself. The server is more lenient than the schema on purpose:
/// it ignores unknown properties, so the schema is the only thing that catches them.
/// </para>
/// <para>
/// ⚠️ The output must be deterministic (stable order, <c>\n</c>, no timestamps): the publish
/// workflow compares it byte-for-byte with the latest <c>schemas/v{n}</c> release to decide whether
/// a new revision exists (G-Q21). Anything that varies between two runs would mint a revision on
/// every push to master.
/// </para>
/// </remarks>
public static class SparkSchemaGenerator
{
    private const string MetaSchema = "https://json-schema.org/draft/2020-12/schema";
    private const string CommentPattern = "^_";

    /// <summary>The six schemas, in a fixed order.</summary>
    public static IReadOnlyList<SparkSchemaFile> Files { get; } =
    [
        new("model", typeof(EntityTypeFile), "Spark model file (App_Data/Model/*.json)"),
        new("security", typeof(SecurityFile), "Spark security configuration (App_Data/security.json)"),
        new("programUnits", typeof(ProgramUnitsConfiguration), "Spark program units (App_Data/programUnits.json)"),
        new("translations", null, "Spark translations (App_Data/translations.json)"),
        new("culture", typeof(CultureFile), "Spark culture (App_Data/culture.json)"),
        new("actions", typeof(Dictionary<string, ActionsFileEntry?>), "Spark action catalogue (App_Data/actions.json)"),
    ];

    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        // The loaders read case-insensitively, but the files are written in camelCase (the model
        // synchronizer writes them that way), and a schema can only name one spelling.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>File name → content, for every schema.</summary>
    public static IReadOnlyDictionary<string, string> Generate()
        => Files.ToDictionary(f => f.FileName, Render, StringComparer.Ordinal);

    /// <summary>Writes every schema to <paramref name="directory"/>, replacing a file only when its content changed.</summary>
    public static void WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (fileName, content) in Generate())
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                // Touched so MSBuild's Inputs/Outputs check sees the target as up to date.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                continue;
            }
            File.WriteAllText(path, content);
        }
    }

    private static string Render(SparkSchemaFile file)
    {
        var body = file.Type is null ? TranslationsSchema() : Export(file.Type);

        // $schema and title first, then the exporter's keywords in the order it wrote them.
        var root = new JsonObject
        {
            ["$schema"] = MetaSchema,
            ["title"] = file.Title,
        };
        foreach (var name in body.Select(p => p.Key).ToList())
        {
            var value = body[name];
            body.Remove(name);
            root[name] = value;
        }
        return root.ToJsonString(WriteOptions) + "\n";
    }

    private static JsonObject Export(Type type)
    {
        var schema = ExportOptions.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = Transform,
        });
        return schema as JsonObject ?? throw new InvalidOperationException($"{type} exported a non-object schema.");
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        var type = Nullable.GetUnderlyingType(context.TypeInfo.Type) ?? context.TypeInfo.Type;

        // Types read by a custom converter export as "anything"; say what the converter accepts.
        if (type == typeof(TranslatedString))
            schema = TranslationKeySchema();
        else if (type == typeof(SparkSubQuery))
            schema = SubQuerySchema();
        else if (type.IsEnum && type.IsDefined(typeof(FlagsAttribute)))
            schema = FlagsSchema(type);
        else if (context.PropertyInfo is { } property
            && property.DeclaringType == typeof(ActionsFileEntry)
            && property.Name == "confirmation")
            schema = new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    new JsonObject { ["type"] = new JsonArray("string", "null") },
                    new JsonObject { ["const"] = false }),
            };

        if (schema is JsonObject obj)
        {
            // "Name": null removes an inherited action. The exporter cannot see the nullability of a
            // dictionary's value type argument, so it is stated here.
            if (type == typeof(ActionsFileEntry) && context.PropertyInfo is null)
                obj["type"] = new JsonArray("object", "null");

            if (IsObjectSchema(obj) && obj.ContainsKey("properties"))
                Close(obj);

            // A model file for a type a library ships is a delta (composition D6): it names the type and
            // its elements, and never states their ids, which the library fixes (D5). The server still
            // requires an id on every element once the layers are composed.
            if (DeltaOptionalId.Contains(type) && obj["required"] is JsonArray required)
            {
                foreach (var id in required.Where(r => r?.GetValue<string>() == "id").ToList())
                    required.Remove(id);
                if (required.Count == 0)
                    obj.Remove("required");
            }

            if (context.Path.IsEmpty && context.PropertyInfo is null)
            {
                // The root: the file may name its own schema, and may carry comments even where its
                // shape is a dictionary (actions.json) rather than a fixed set of properties.
                var properties = obj["properties"] as JsonObject;
                if (properties is null)
                {
                    properties = new JsonObject();
                    obj["properties"] = properties;
                }
                properties["$schema"] = new JsonObject { ["type"] = "string" };
                obj["patternProperties"] ??= new JsonObject { [CommentPattern] = new JsonObject() };
            }
        }

        return schema;
    }

    /// <summary>The model elements whose <c>id</c> a delta on a library type leaves out.</summary>
    private static readonly HashSet<Type> DeltaOptionalId =
    [
        typeof(EntityTypeDefinition), typeof(EntityAttributeDefinition), typeof(AttributeTab), typeof(AttributeGroup), typeof(SparkQuery),
    ];

    private static bool IsObjectSchema(JsonObject schema) => schema["type"] switch
    {
        JsonValue value => value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() == "object",
        JsonArray array => array.Any(t => t?.GetValue<string>() == "object"),
        _ => false,
    };

    private static void Close(JsonObject schema)
    {
        schema["patternProperties"] ??= new JsonObject { [CommentPattern] = new JsonObject() };
        schema["additionalProperties"] ??= false;
    }

    /// <summary>
    /// A <see cref="TranslatedString"/> in an App_Data file is a translations.json key. The converter
    /// also reads the old language → text object, but every loader refuses that form
    /// (<c>SparkText.RejectInlineText</c>, #467 D1), so the schema does too.
    /// </summary>
    private static JsonObject TranslationKeySchema() => new()
    {
        ["type"] = new JsonArray("string", "null"),
        ["description"] = "A translations.json key.",
    };

    /// <summary>What <c>SparkSubQueryJsonConverter</c> reads: a query alias, or an object naming one.</summary>
    private static JsonObject SubQuerySchema()
    {
        var selectionMode = ExportOptions.GetJsonSchemaAsNode(typeof(SparkSelectionMode));
        var entry = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["selectionMode"] = new JsonObject { ["anyOf"] = new JsonArray(selectionMode, new JsonObject { ["type"] = "null" }) },
                ["parentReference"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            },
            ["required"] = new JsonArray("query"),
        };
        Close(entry);
        return new JsonObject
        {
            ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, entry),
        };
    }

    /// <summary>
    /// A <c>[Flags]</c> enum is written as its names joined by commas (<c>"Query, PersistentObject"</c>),
    /// which an <c>enum</c> keyword cannot express. The pattern accepts any combination of names;
    /// <c>examples</c> keep the editor's completion list.
    /// </summary>
    private static JsonObject FlagsSchema(Type type)
    {
        var names = type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .OrderBy(f => Convert.ToInt64(f.GetRawConstantValue()))
            .Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? f.Name)
            .ToList();
        var name = $"(?:{string.Join("|", names)})";

        var examples = new JsonArray();
        foreach (var n in names)
            examples.Add(n);
        var combined = string.Join(", ", names.Where(n => n != "None"));
        if (combined.Contains(','))
            examples.Add(combined);

        return new JsonObject
        {
            ["type"] = "string",
            ["pattern"] = $"^\\s*{name}(?:\\s*,\\s*{name})*\\s*$",
            ["examples"] = examples,
        };
    }

    /// <summary>
    /// translations.json is a tree: every object holds either only strings (a leaf, language → text)
    /// or only objects (a namespace). The generators and the run time report anything else
    /// (<c>SparkTranslationLayers</c>, composition D10), and skip underscore properties as comments. The schema
    /// keeps a comment a string, as every file in the repository writes it. The run time also takes
    /// <c>null</c> for a namespace a library ships (composition D3); the schema learns it with M10.
    /// </summary>
    private static JsonObject TranslationsSchema()
    {
        static JsonObject Comments() => new() { [CommentPattern] = new JsonObject { ["type"] = "string" } };

        static JsonObject Branch(JsonNode values)
        {
            var branch = new JsonObject
            {
                ["patternProperties"] = Comments(),
                ["additionalProperties"] = values,
            };
            return branch;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["$schema"] = new JsonObject { ["type"] = "string" } },
            ["patternProperties"] = Comments(),
            ["additionalProperties"] = new JsonObject { ["$ref"] = "#/$defs/node" },
            ["$defs"] = new JsonObject
            {
                ["node"] = new JsonObject
                {
                    ["type"] = "object",
                    ["minProperties"] = 1,
                    ["anyOf"] = new JsonArray(
                        Branch(new JsonObject { ["type"] = "string" }),
                        Branch(new JsonObject { ["$ref"] = "#/$defs/node" })),
                },
            },
        };
    }
}

/// <summary>
/// <c>App_Data/culture.json</c> as <c>CultureLoader</c> reads it. The loader walks a
/// <see cref="JsonDocument"/> rather than deserializing a type, so this mirrors the two properties
/// it looks for; a guard test keeps the two in step.
/// </summary>
public sealed class CultureFile
{
    /// <summary>The language codes, e.g. <c>["en", "fr", "nl"]</c>. Each name is the translations.json key <c>culture.languages.{code}</c>.</summary>
    public string[]? Languages { get; set; }

    public string? DefaultLanguage { get; set; }
}

/// <summary>
/// One action in <c>App_Data/actions.json</c>, as <c>ActionsCatalogueLoader</c> binds it. The loader
/// composes the layers as JSON nodes and binds a fixed list of known properties, so this mirrors that
/// list; a guard test keeps the two in step. A null entry removes the action.
/// </summary>
public sealed class ActionsFileEntry
{
    /// <summary>A translations.json key; defaults to <c>actions.{name}.label</c>.</summary>
    public string? Label { get; set; }

    /// <summary>A translations.json key; defaults to <c>actions.{name}.description</c>.</summary>
    public string? Description { get; set; }

    /// <summary>A translations.json key, or <c>false</c> to never ask (even when <c>actions.{name}.confirmation</c> is translated).</summary>
    public JsonElement? Confirmation { get; set; }

    public string? Icon { get; set; }

    public ActionShowedOn? ShowedOn { get; set; }

    /// <summary>A cardinality expression over the selected rows: <c>=1</c>, <c>&gt;0</c>, <c>&lt;=5</c>, <c>1&lt;X&lt;5</c>.</summary>
    public string? SelectionRule { get; set; }

    public bool? RefreshOnCompleted { get; set; }

    /// <summary>Presentation only: <c>primary</c>, <c>secondary</c>, <c>danger</c>, <c>warning</c>.</summary>
    public string? Variant { get; set; }

    public int? Offset { get; set; }
}

/// <summary>Where an action is shown, as <c>actions.json</c> spells it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActionShowedOn>))]
public enum ActionShowedOn
{
    [JsonStringEnumMemberName("detail")] Detail,
    [JsonStringEnumMemberName("query")] Query,
    [JsonStringEnumMemberName("both")] Both,
}
