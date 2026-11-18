using System.Text;
using System.Text.Json.Nodes;

namespace MintPlayer.Spark.Cli.Webhooks;

/// <summary>A type a property or event data refers to.</summary>
public abstract record WebhookTypeRef(bool Nullable)
{
    public abstract WebhookTypeRef AsNullable();
}

/// <summary><c>string</c>, <c>integer</c>, <c>number</c> or <c>boolean</c>; <see cref="Format"/> is the JSON Schema format (date-time, uuid, …).</summary>
public sealed record WebhookPrimitive(string Kind, string? Format, bool Nullable) : WebhookTypeRef(Nullable)
{
    public override WebhookTypeRef AsNullable() => this with { Nullable = true };
}

public sealed record WebhookArray(WebhookTypeRef Item, bool Nullable) : WebhookTypeRef(Nullable)
{
    public override WebhookTypeRef AsNullable() => this with { Nullable = true };
}

/// <summary>An object with no fixed properties: a string-keyed map.</summary>
public sealed record WebhookMap(WebhookTypeRef Value, bool Nullable) : WebhookTypeRef(Nullable)
{
    public override WebhookTypeRef AsNullable() => this with { Nullable = true };
}

/// <summary>A generated object or enum type.</summary>
public sealed record WebhookNamed(string Name, bool Nullable) : WebhookTypeRef(Nullable)
{
    public override WebhookTypeRef AsNullable() => this with { Nullable = true };
}

/// <summary>Anything the schema does not pin down (a union, no type): raw JSON.</summary>
public sealed record WebhookAny(bool Nullable) : WebhookTypeRef(Nullable)
{
    public override WebhookTypeRef AsNullable() => this with { Nullable = true };
}

public sealed record WebhookProperty(string WireName, string Name, WebhookTypeRef Type, string? Description);

public abstract record WebhookTypeDefinition(string Name, string? Description);

public sealed record WebhookObjectType(string Name, string? Description, IReadOnlyList<WebhookProperty> Properties) : WebhookTypeDefinition(Name, Description);

/// <summary>A string enum: the wire values and the member names generated for them.</summary>
public sealed record WebhookEnumType(string Name, string? Description, IReadOnlyList<(string Wire, string Member)> Values) : WebhookTypeDefinition(Name, Description);

public sealed record WebhookEventModel(string Name, string MethodName, string? Summary, string? Permission, WebhookTypeRef Data);

/// <summary>
/// The site's catalog (<c>GET /spark/apps/webhooks/catalog</c>, OpenAPI 3.1 <c>webhooks</c>) read into events and the types
/// their <c>data</c> needs. Both emitters work from this, so C# and TypeScript name everything alike (D8).
/// </summary>
public sealed class WebhookCatalogModel
{
    private readonly List<WebhookTypeDefinition> types = [];
    private readonly HashSet<string> takenNames = new(StringComparer.Ordinal);
    private readonly Dictionary<JsonNode, WebhookNamed> named = new(ReferenceEqualityComparer.Instance);
    // A nested type is inlined wherever it is used; one with the same title and shape is the same type.
    private readonly List<(string Title, JsonNode Shape, WebhookNamed Type)> byShape = [];
    // The data schema being read: a $ref points into it (JSON pointer from the data root).
    private JsonObject? currentRoot;

    private WebhookCatalogModel(string title, string version)
    {
        Title = title;
        Version = version;
    }

    public string Title { get; }
    public string Version { get; }
    public List<WebhookEventModel> Events { get; } = [];
    public IReadOnlyList<WebhookTypeDefinition> Types => types;

    /// <summary>Reads the catalog. Throws <see cref="FormatException"/> when it is not an OpenAPI document with <c>webhooks</c>.</summary>
    public static WebhookCatalogModel Read(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new FormatException("The catalog is not JSON: " + e.Message, e);
        }
        if (root is not JsonObject document || document["webhooks"] is not JsonObject webhooks)
            throw new FormatException("The catalog has no \"webhooks\": is this a Spark Apps catalog (/spark/apps/webhooks/catalog)?");

        var info = document["info"] as JsonObject;
        var model = new WebhookCatalogModel(info?["title"]?.GetValue<string>() ?? "Webhooks", info?["version"]?.GetValue<string>() ?? "1.0.0");
        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, item) in webhooks.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (item?["post"] is not JsonObject operation)
                continue;
            var envelope = operation["requestBody"]?["content"]?["application/json"]?["schema"] as JsonObject;
            var dataSchema = envelope?["properties"]?["data"] as JsonObject;
            var data = dataSchema is null ? new WebhookAny(false) : model.Visit(dataSchema, dataSchema, Pascal(name));
            var method = Unique(methodNames, Pascal(name));
            var summary = operation["summary"]?.GetValue<string>();
            model.Events.Add(new WebhookEventModel(name, method, summary == name ? null : summary, operation["x-spark-permission"]?.GetValue<string>(), data));
        }
        return model;
    }

    private WebhookTypeRef Visit(JsonObject schema, JsonObject root, string nameHint)
    {
        currentRoot = root;
        if (schema["$ref"]?.GetValue<string>() is { } pointer)
        {
            var target = Resolve(root, pointer) ?? throw new FormatException($"Unresolvable $ref '{pointer}'.");
            var resolved = Visit(target, root, nameHint);
            return Nullable(schema) ? resolved.AsNullable() : resolved;
        }

        var nullable = Nullable(schema);
        var kinds = Kinds(schema);

        if (schema["enum"] is JsonArray values && values.All(v => v is null || v.GetValueKind() == System.Text.Json.JsonValueKind.String))
        {
            if (named.TryGetValue(schema, out var known) || SameShape(schema, out known))
                return known with { Nullable = nullable || values.Any(v => v is null) };
            var name = Unique(takenNames, schema["title"]?.GetValue<string>() is { } t ? Pascal(t) : nameHint);
            var members = new HashSet<string>(StringComparer.Ordinal);
            var enumValues = values.Where(v => v is not null).Select(v => v!.GetValue<string>()).Select(v => (v, Unique(members, Pascal(v)))).ToList();
            types.Add(new WebhookEnumType(name, Description(schema), enumValues));
            var reference = new WebhookNamed(name, false);
            Remember(schema, reference);
            return reference with { Nullable = nullable || values.Any(v => v is null) };
        }

        if (kinds.Count != 1)
            return new WebhookAny(nullable);

        switch (kinds[0])
        {
            case "string" or "integer" or "number" or "boolean":
                return new WebhookPrimitive(kinds[0], schema["format"]?.GetValue<string>(), nullable);
            case "array":
                var items = schema["items"] as JsonObject;
                return new WebhookArray(items is null ? new WebhookAny(false) : Visit(items, root, Singular(nameHint)), nullable);
            case "object":
                if (named.TryGetValue(schema, out var existing) || SameShape(schema, out existing))
                    return existing with { Nullable = nullable };
                if (schema["properties"] is not JsonObject properties)
                {
                    return schema["additionalProperties"] is JsonObject valueSchema
                        ? new WebhookMap(Visit(valueSchema, root, nameHint + "Value"), nullable)
                        : new WebhookMap(new WebhookAny(false), nullable);
                }
                var typeName = Unique(takenNames, schema["title"]?.GetValue<string>() is { } title ? Pascal(title) : nameHint);
                var reference = new WebhookNamed(typeName, false);
                // Registered before the properties, so a recursive $ref resolves to this type.
                Remember(schema, reference);
                var index = types.Count;
                types.Add(new WebhookObjectType(typeName, Description(schema), []));
                var propertyNames = new HashSet<string>(StringComparer.Ordinal) { typeName };
                var list = new List<WebhookProperty>();
                foreach (var (wire, node) in properties)
                {
                    if (node is not JsonObject propertySchema)
                        continue;
                    var propertyName = Unique(propertyNames, Pascal(wire), suffix: "Value");
                    list.Add(new WebhookProperty(wire, propertyName, Visit(propertySchema, root, typeName + Pascal(wire)), Description(propertySchema)));
                }
                types[index] = new WebhookObjectType(typeName, Description(schema), list);
                return reference with { Nullable = nullable };
            default:
                return new WebhookAny(nullable);
        }
    }

    private void Remember(JsonObject schema, WebhookNamed reference)
    {
        named[schema] = reference;
        if (schema["title"]?.GetValue<string>() is { } title)
            byShape.Add((title, Shape(schema), reference));
    }

    private bool SameShape(JsonObject schema, out WebhookNamed reference)
    {
        reference = null!;
        if (schema["title"]?.GetValue<string>() is not { } title)
            return false;
        var shape = Shape(schema);
        foreach (var (knownTitle, knownShape, type) in byShape)
        {
            if (knownTitle == title && JsonNode.DeepEquals(knownShape, shape))
            {
                reference = type;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The schema without what differs per use: <c>null</c> in its type, the property's description, and how a nested titled type is
    /// written (inline or as a <c>$ref</c>, which the exporter picks by position): each becomes just its title.
    /// </summary>
    private JsonNode Shape(JsonObject schema) => Canonical(schema, top: true);

    private JsonObject Canonical(JsonObject schema, bool top)
    {
        if (schema["$ref"]?.GetValue<string>() is { } pointer && Resolve(currentRoot!, pointer) is { } target)
            return Canonical(target, top: false);
        if (!top && schema["title"]?.GetValue<string>() is { } title)
            return new JsonObject { ["title"] = title };

        var shape = new JsonObject();
        foreach (var (key, value) in schema)
        {
            if (key == "description")
                continue;
            shape[key] = key switch
            {
                "type" when value is JsonArray types => NonNull(types),
                "enum" when value is JsonArray values => new JsonArray([.. values.Where(v => v is not null).Select(v => v!.DeepClone())]),
                "items" or "additionalProperties" when value is JsonObject child => Canonical(child, top: false),
                "properties" when value is JsonObject properties => new JsonObject(properties.Select(p =>
                    KeyValuePair.Create(p.Key, p.Value is JsonObject child ? (JsonNode?)Canonical(child, top: false) : p.Value?.DeepClone()))),
                _ => value?.DeepClone(),
            };
        }
        return shape;
    }

    private static JsonNode NonNull(JsonArray types)
    {
        var rest = types.Select(t => t!.GetValue<string>()).Where(t => t != "null").ToList();
        return rest.Count == 1 ? JsonValue.Create(rest[0]) : new JsonArray([.. rest.Select(t => (JsonNode)JsonValue.Create(t))]);
    }

    private static List<string> Kinds(JsonObject schema) => schema["type"] switch
    {
        JsonValue value => value.GetValue<string>() == "null" ? [] : [value.GetValue<string>()],
        JsonArray array => [.. array.Select(t => t!.GetValue<string>()).Where(t => t != "null")],
        _ => schema["properties"] is not null ? ["object"] : [],
    };

    private static bool Nullable(JsonObject schema) => schema["type"] switch
    {
        JsonValue value => value.GetValue<string>() == "null",
        JsonArray array => array.Any(t => t!.GetValue<string>() == "null"),
        _ => false,
    };

    private static string? Description(JsonObject schema) => schema["description"]?.GetValue<string>();

    /// <summary>A JSON pointer within the data schema (<c>#</c> is its root, which is how the exporter writes recursion).</summary>
    private static JsonObject? Resolve(JsonObject root, string reference)
    {
        if (!reference.StartsWith('#'))
            return null;
        JsonNode? node = root;
        foreach (var segment in reference[1..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = segment.Replace("~1", "/").Replace("~0", "~");
            node = node switch
            {
                JsonObject obj => obj[key],
                JsonArray array when int.TryParse(key, out var i) && i < array.Count => array[i],
                _ => null,
            };
        }
        return node as JsonObject;
    }

    /// <summary><c>report.completed</c> → <c>ReportCompleted</c>; <c>lineRate</c> → <c>LineRate</c>; a leading digit gets <c>_</c>.</summary>
    public static string Pascal(string value)
    {
        var builder = new StringBuilder();
        var upper = true;
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                upper = true;
                continue;
            }
            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        if (builder.Length == 0)
            return "Value";
        return char.IsAsciiDigit(builder[0]) ? "_" + builder : builder.ToString();
    }

    private static string Singular(string name) => name.EndsWith("ies", StringComparison.Ordinal) ? name[..^3] + "y"
        : name.EndsWith('s') && !name.EndsWith("ss", StringComparison.Ordinal) ? name[..^1]
        : name + "Item";

    private static string Unique(HashSet<string> taken, string name, string suffix = "")
    {
        if (taken.Add(name))
            return name;
        if (suffix.Length > 0 && taken.Add(name + suffix))
            return name + suffix;
        for (var i = 2; ; i++)
            if (taken.Add(name + i))
                return name + i;
    }
}
