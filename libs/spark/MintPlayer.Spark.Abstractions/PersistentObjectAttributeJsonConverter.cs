using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Polymorphic binding for <see cref="PersistentObjectAttribute"/> deserialization, built as
/// a <see cref="JsonConverterFactory"/> so it only intercepts the <i>declared</i> base type
/// (<c>List&lt;PersistentObjectAttribute&gt;</c> element type). When STJ recurses into the
/// concrete subclass (e.g. <see cref="PersistentObjectAttributeAsDetail"/>),
/// <see cref="CanConvert"/> returns <c>false</c> and STJ falls through to its default
/// reflection-based converter, which includes the derived members (<c>Object</c>,
/// <c>Objects</c>) automatically.
/// </summary>
/// <remarks>
/// Discriminator: the <c>dataType</c> JSON property. <c>"AsDetail"</c> maps to
/// <see cref="PersistentObjectAttributeAsDetail"/>; anything else (or absent) maps to the
/// plain base class. Buffering through <see cref="JsonDocument"/> is cheap for the small
/// attribute objects we handle here.
/// </remarks>
public sealed class PersistentObjectAttributeJsonConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert == typeof(PersistentObjectAttribute);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => new Inner();

    private sealed class Inner : JsonConverter<PersistentObjectAttribute>
    {
        public override PersistentObjectAttribute? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected StartObject for PersistentObjectAttribute");

            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            // Discriminator lookup is case-insensitive so it binds against STJ's default
            // PascalCase output and camelCase producers alike.
            string? dataType = null;
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, "DataType", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    dataType = prop.Value.GetString();
                    break;
                }
            }

            var targetType = string.Equals(dataType, "AsDetail", StringComparison.Ordinal)
                ? typeof(PersistentObjectAttributeAsDetail)
                : typeof(PersistentObjectAttribute);

            // CanConvert returns false for `targetType` when it's the subclass, AND true for
            // the base — but inner recursion here will NOT re-enter Read() because STJ
            // resolves the converter via the factory's CanConvert, which only matches the
            // base for the base. For the subclass, STJ uses default reflection binding —
            // exactly what we want. For the base path (non-AsDetail), we return a freshly
            // constructed instance by re-serializing the buffered JSON and asking STJ to
            // bind into `PersistentObjectAttribute`; since the factory DOES match that type,
            // we'd loop — so we hand-bind the base instead.
            if (targetType == typeof(PersistentObjectAttributeAsDetail))
            {
                return JsonSerializer.Deserialize<PersistentObjectAttributeAsDetail>(root.GetRawText(), options);
            }

            return BindBase(root, options);
        }

        public override void Write(Utf8JsonWriter writer, PersistentObjectAttribute value, JsonSerializerOptions options)
        {
            // CanConvert is false for derived types, so we only get here for the declared
            // base. If the runtime instance is a subclass (AsDetail), forward to the
            // subclass serializer — STJ will pick the default converter for the subclass
            // and include all its fields.
            var runtimeType = value.GetType();
            if (runtimeType != typeof(PersistentObjectAttribute))
            {
                JsonSerializer.Serialize(writer, value, runtimeType, options);
                return;
            }

            BindBaseWrite(writer, value, options);
        }

        /// <summary>
        /// Hand-binds a <see cref="PersistentObjectAttribute"/> from <see cref="JsonElement"/>.
        /// Mirrors the default reflection converter's behavior for our known property set —
        /// needed because round-tripping the root back through
        /// <c>JsonSerializer.Deserialize&lt;PersistentObjectAttribute&gt;</c> would loop via
        /// the factory.
        /// </summary>
        private static PersistentObjectAttribute BindBase(JsonElement root, JsonSerializerOptions options)
        {
            var attr = new PersistentObjectAttribute
            {
                Name = RequireString(root, "name"),
            };
            PopulateFromJson(attr, root, options);
            return attr;
        }

        private static void BindBaseWrite(Utf8JsonWriter writer, PersistentObjectAttribute value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            WriteSharedFields(writer, value, options);
            writer.WriteEndObject();
        }

        private static void PopulateFromJson(PersistentObjectAttribute attr, JsonElement root, JsonSerializerOptions options)
        {
            // Match property names case-insensitively AND honor any configured naming
            // policy. This keeps us wire-compatible with STJ's default PascalCase output as
            // well as camelCase payloads from clients that configured JsonNamingPolicy.CamelCase.
            foreach (var prop in root.EnumerateObject())
            {
                var clrName = ResolveClrName(prop.Name, options);
                switch (clrName)
                {
                    case "Id":              attr.Id             = ReadNullableString(prop.Value); break;
                    case "Name":            /* already set via required */                         break;
                    case "Label":           attr.Label          = Deserialize<TranslatedString>(prop.Value, options); break;
                    case "Value":           attr.Value          = DeserializeValue(prop.Value); break;
                    case "DataType":        attr.DataType       = prop.Value.GetString() ?? "string"; break;
                    case "IsArray":         attr.IsArray        = prop.Value.GetBoolean(); break;
                    case "IsRequired":      attr.IsRequired     = prop.Value.GetBoolean(); break;
                    case "IsVisible":       attr.IsVisible      = prop.Value.GetBoolean(); break;
                    case "IsReadOnly":      attr.IsReadOnly     = prop.Value.GetBoolean(); break;
                    case "IsValueChanged":  attr.IsValueChanged = prop.Value.GetBoolean(); break;
                    case "Order":           attr.Order          = prop.Value.GetInt32(); break;
                    case "Query":           attr.Query          = ReadNullableString(prop.Value); break;
                    case "Breadcrumb":      attr.Breadcrumb     = ReadNullableString(prop.Value); break;
                    case "ShowedOn":        attr.ShowedOn       = Deserialize<EShowedOn>(prop.Value, options); break;
                    case "Rules":           attr.Rules          = Deserialize<ValidationRule[]>(prop.Value, options) ?? []; break;
                    case "Group":           attr.Group          = prop.Value.ValueKind == JsonValueKind.Null ? null : Deserialize<Guid?>(prop.Value, options); break;
                    case "Renderer":        attr.Renderer       = ReadNullableString(prop.Value); break;
                    case "RendererOptions": attr.RendererOptions = Deserialize<Dictionary<string, object>>(prop.Value, options); break;
                }
            }
        }

        private static void WriteSharedFields(Utf8JsonWriter writer, PersistentObjectAttribute value, JsonSerializerOptions options)
        {
            WritePropertyName(writer, "Id", options);              writer.WriteStringValue(value.Id);
            WritePropertyName(writer, "Name", options);            writer.WriteStringValue(value.Name);
            WritePropertyName(writer, "Label", options);           JsonSerializer.Serialize(writer, value.Label, options);
            WritePropertyName(writer, "Value", options);           JsonSerializer.Serialize(writer, value.Value, options);
            WritePropertyName(writer, "DataType", options);        writer.WriteStringValue(value.DataType);
            WritePropertyName(writer, "IsArray", options);         writer.WriteBooleanValue(value.IsArray);
            WritePropertyName(writer, "IsRequired", options);      writer.WriteBooleanValue(value.IsRequired);
            WritePropertyName(writer, "IsVisible", options);       writer.WriteBooleanValue(value.IsVisible);
            WritePropertyName(writer, "IsReadOnly", options);      writer.WriteBooleanValue(value.IsReadOnly);
            WritePropertyName(writer, "IsValueChanged", options);  writer.WriteBooleanValue(value.IsValueChanged);
            WritePropertyName(writer, "Order", options);           writer.WriteNumberValue(value.Order);
            WritePropertyName(writer, "Query", options);           writer.WriteStringValue(value.Query);
            WritePropertyName(writer, "Breadcrumb", options);      writer.WriteStringValue(value.Breadcrumb);
            WritePropertyName(writer, "ShowedOn", options);        JsonSerializer.Serialize(writer, value.ShowedOn, options);
            WritePropertyName(writer, "Rules", options);           JsonSerializer.Serialize(writer, value.Rules, options);
            WritePropertyName(writer, "Group", options);
            if (value.Group is { } g) writer.WriteStringValue(g.ToString()); else writer.WriteNullValue();
            WritePropertyName(writer, "Renderer", options);        writer.WriteStringValue(value.Renderer);
            WritePropertyName(writer, "RendererOptions", options); JsonSerializer.Serialize(writer, value.RendererOptions, options);
        }

        private static void WritePropertyName(Utf8JsonWriter writer, string clrName, JsonSerializerOptions options)
        {
            var wireName = options.PropertyNamingPolicy is { } policy ? policy.ConvertName(clrName) : clrName;
            writer.WritePropertyName(wireName);
        }

        /// <summary>
        /// Maps an on-wire property name back to the declared CLR name. Checks the naming
        /// policy's round-trip (Name → wireName) against every known field, then falls back
        /// to a case-insensitive comparison so STJ's default PascalCase output binds cleanly
        /// regardless of how the producer was configured.
        /// </summary>
        private static string ResolveClrName(string wireName, JsonSerializerOptions options)
        {
            if (options.PropertyNameCaseInsensitive || options.PropertyNamingPolicy is not null)
            {
                foreach (var clrName in KnownFieldNames)
                {
                    var expected = options.PropertyNamingPolicy is { } p ? p.ConvertName(clrName) : clrName;
                    if (string.Equals(wireName, expected, options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        return clrName;
                }
            }

            // Case-insensitive fallback — STJ's default behavior for object binding.
            foreach (var clrName in KnownFieldNames)
                if (string.Equals(wireName, clrName, StringComparison.OrdinalIgnoreCase))
                    return clrName;

            return wireName;
        }

        private static readonly string[] KnownFieldNames =
        [
            "Id", "Name", "Label", "Value", "DataType", "IsArray", "IsRequired", "IsVisible",
            "IsReadOnly", "IsValueChanged", "Order", "Query", "Breadcrumb", "ShowedOn",
            "Rules", "Group", "Renderer", "RendererOptions",
        ];

        private static string? ReadNullableString(JsonElement el)
            => el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : el.GetString();

        private static string RequireString(JsonElement root, string clrName)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, clrName, StringComparison.OrdinalIgnoreCase))
                    return prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()!
                        : throw new JsonException($"PersistentObjectAttribute '{clrName}' must be a string");
            }
            throw new JsonException($"PersistentObjectAttribute is missing required string property '{clrName}'");
        }

        private static T? Deserialize<T>(JsonElement el, JsonSerializerOptions options)
            => el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                ? default
                : JsonSerializer.Deserialize<T>(el.GetRawText(), options);

        private static object? DeserializeValue(JsonElement el)
            => el.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                // Keep the raw JsonElement so downstream mapper code can inspect it with
                // the same code paths it uses for STJ-deserialized payloads.
                _ => el.Clone(),
            };
    }
}
