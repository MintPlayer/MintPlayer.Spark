using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using MintPlayer.Webhooks;

namespace MintPlayer.Spark.Apps.Catalog;

/// <summary>One declared event: its wire name, CLR type, permission and the JSON schema of its <c>data</c>.</summary>
public sealed class WebhookEventDescriptor
{
    internal WebhookEventDescriptor(string name, Type type, string? permission, string? description, JsonObject schema)
    {
        Name = name;
        Type = type;
        Permission = permission;
        Description = description;
        Schema = schema;
    }

    public string Name { get; }
    public Type Type { get; }
    public string? Permission { get; }
    public string? Description { get; }

    /// <summary>The schema of <c>data</c> (S1: <see cref="JsonSchemaExporter"/> with <see cref="WebhookJson.Options"/>).</summary>
    public JsonObject Schema { get; }
}

/// <summary>
/// The site's webhook events (D19). Built once at startup from the <see cref="AppsBuilder"/> registrations; two
/// types with the same event name, or one type under two names, refuse startup.
/// </summary>
public sealed class WebhookEventCatalog
{
    /// <summary>The built-in event every endpoint receives on <c>ping</c>; it needs no declaration.</summary>
    public const string PingEvent = WebhookProcessor.PingEventType;

    private readonly Dictionary<string, WebhookEventDescriptor> byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, WebhookEventDescriptor> byType = [];

    internal WebhookEventCatalog(IEnumerable<WebhookEventRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            if (byType.TryGetValue(registration.Type, out var existing))
            {
                if (existing.Name == registration.Name)
                    continue;
                throw new InvalidOperationException($"{registration.Type.Name} is declared as webhook event '{existing.Name}' and '{registration.Name}'.");
            }
            if (registration.Name == PingEvent)
                throw new InvalidOperationException($"'{PingEvent}' is the built-in ping event; {registration.Type.Name} cannot use it.");
            if (byName.TryGetValue(registration.Name, out var clash))
                throw new InvalidOperationException($"Webhook event '{registration.Name}' is declared by both {clash.Type.Name} and {registration.Type.Name}.");

            var descriptor = new WebhookEventDescriptor(registration.Name, registration.Type, registration.Permission, registration.Description, ExportSchema(registration.Type));
            byName.Add(descriptor.Name, descriptor);
            byType.Add(descriptor.Type, descriptor);
        }
    }

    public IReadOnlyCollection<WebhookEventDescriptor> Events => byName.Values;

    public WebhookEventDescriptor? Find(string name) => byName.GetValueOrDefault(name);

    public WebhookEventDescriptor? Find(Type type) => byType.GetValueOrDefault(type);

    /// <summary>True for a declared event or <c>ping</c>.</summary>
    public bool IsKnown(string name) => name == PingEvent || byName.ContainsKey(name);

    /// <summary>
    /// The catalog as an OpenAPI 3.1 document with one <c>webhooks</c> entry per event (the format codegen reads, D19).
    /// Each request body is the Standard Webhooks envelope with this event's <c>data</c>.
    /// </summary>
    public JsonObject ToOpenApi(string title, string version = "1.0.0")
    {
        var webhooks = new JsonObject();
        foreach (var descriptor in byName.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            var operation = new JsonObject
            {
                ["operationId"] = descriptor.Name,
                ["summary"] = descriptor.Description ?? descriptor.Name,
                ["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject { ["schema"] = Envelope(descriptor.Name, descriptor.Schema.DeepClone()) },
                    },
                },
                ["responses"] = new JsonObject { ["2XX"] = new JsonObject { ["description"] = "Received." } },
            };
            if (descriptor.Permission is not null)
                operation["x-spark-permission"] = descriptor.Permission;
            webhooks[descriptor.Name] = new JsonObject { ["post"] = operation };
        }

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject { ["title"] = title, ["version"] = version },
            ["webhooks"] = webhooks,
        };
    }

    private static JsonObject Envelope(string name, JsonNode data) => new()
    {
        ["type"] = "object",
        ["required"] = new JsonArray("type", "timestamp", "data"),
        ["properties"] = new JsonObject
        {
            ["type"] = new JsonObject { ["const"] = name },
            ["timestamp"] = new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            ["data"] = data,
        },
    };

    /// <summary>The class name codegen gives <c>data</c>: the type's name, and <c>ReportCreated</c> for <c>EntityCreated&lt;Report&gt;</c>.</summary>
    internal static string Title(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;
        var definition = type.Name[..type.Name.IndexOf('`')];
        if (definition.StartsWith("Entity", StringComparison.Ordinal))
            definition = definition["Entity".Length..];
        return type.GetGenericArguments()[0].Name + definition;
    }

    private static JsonObject ExportSchema(Type type)
    {
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                if (node is not JsonObject schema)
                    return node;
                if (context.Path.IsEmpty)
                    schema.Insert(0, "title", Title(type));
                var description = (context.PropertyInfo?.AttributeProvider ?? context.TypeInfo.Type)
                    .GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
                    .OfType<DescriptionAttribute>().FirstOrDefault()?.Description;
                if (description is not null && !schema.ContainsKey("description"))
                    schema["description"] = description;
                return schema;
            },
        };
        return WebhookJson.Options.GetJsonSchemaAsNode(type, exporter) as JsonObject
            ?? throw new InvalidOperationException($"{type.Name} has no object schema; a webhook event's data is a JSON object.");
    }
}

/// <summary>An event as registered on <see cref="AppsBuilder"/>.</summary>
internal sealed record WebhookEventRegistration(string Name, Type Type, string? Permission, string? Description)
{
    public static WebhookEventRegistration FromAttribute(Type type)
    {
        var attribute = type.GetCustomAttribute<WebhookEventAttribute>()
            ?? throw new InvalidOperationException($"{type.Name} has no [WebhookEvent] attribute.");
        if (string.IsNullOrWhiteSpace(attribute.Name))
            throw new InvalidOperationException($"{type.Name}: [WebhookEvent] needs a name.");
        return new(attribute.Name, type, attribute.Permission, attribute.Description);
    }
}
