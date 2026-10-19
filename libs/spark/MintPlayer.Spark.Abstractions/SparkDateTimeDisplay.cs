using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// How a <c>datetime</c> attribute is shown (commerce PRD X16, D34): the zone of its main rendering,
/// and optionally a second rendering in another zone, the <see cref="Hint"/> ("21:00 (20:00 London)").
/// Rendered by ng-spark's <c>&lt;spark-date-time&gt;</c> in grids and on detail pages, and by
/// <c>ISparkDateTimeFormatter</c> on the server, where "viewer" means the recipient (X14).
/// </summary>
/// <remarks>
/// <para>
/// Hand-set in the model JSON and preserved across synchronize (like <c>editMode</c>); absent means
/// the viewer's zone with no hint, which is what every date has always done.
/// </para>
/// <code>
/// "dateTimeDisplay": { "primary": "viewer", "hint": { "zoneFrom": "VenueTimeZoneId" } }
/// </code>
/// <para>
/// The hint is hidden when both renderings give the same wall clock at that instant, so a viewer in
/// the venue's own zone sees one time. The comparison is by offset on the value's date, not by zone name.
/// </para>
/// </remarks>
public sealed class SparkDateTimeDisplay
{
    /// <summary>The main rendering: <c>"viewer"</c> (the default), <c>"stored"</c>, <c>{ "zone": "&lt;IANA id&gt;" }</c> or <c>{ "zoneFrom": "&lt;attribute&gt;" }</c>.</summary>
    public SparkDateTimeZone? Primary { get; set; }

    /// <summary>An optional second rendering, in the same forms as <see cref="Primary"/>.</summary>
    public SparkDateTimeZone? Hint { get; set; }
}

/// <summary>Which wall clock a rendering of an instant shows.</summary>
public enum ESparkDateTimeZoneKind
{
    /// <summary>The viewer's zone; on the server, the recipient's (<c>ISparkRecipientLocale</c>).</summary>
    Viewer,
    /// <summary>The wall clock the value was stored with, and its offset.</summary>
    Stored,
    /// <summary>A fixed IANA zone, <see cref="SparkDateTimeZone.Zone"/>.</summary>
    Zone,
    /// <summary>The IANA zone held by another attribute of the same object, <see cref="SparkDateTimeZone.ZoneFrom"/>.</summary>
    ZoneFrom,
}

/// <summary>
/// One rendering's zone: <c>"viewer"</c> | <c>"stored"</c> | <c>{ "zone": "Europe/Brussels" }</c> |
/// <c>{ "zoneFrom": "VenueTimeZoneId" }</c>. Written back in the same form, so a synchronize is a fixed point.
/// </summary>
[JsonConverter(typeof(SparkDateTimeZoneJsonConverter))]
public sealed record SparkDateTimeZone
{
    private SparkDateTimeZone(ESparkDateTimeZoneKind kind, string? value)
    {
        Kind = kind;
        Value = value;
    }

    public ESparkDateTimeZoneKind Kind { get; }

    private string? Value { get; }

    /// <summary>The IANA id, for <see cref="ESparkDateTimeZoneKind.Zone"/>.</summary>
    public string? Zone => Kind == ESparkDateTimeZoneKind.Zone ? Value : null;

    /// <summary>The attribute holding an IANA id, for <see cref="ESparkDateTimeZoneKind.ZoneFrom"/>.</summary>
    public string? ZoneFrom => Kind == ESparkDateTimeZoneKind.ZoneFrom ? Value : null;

    public static SparkDateTimeZone Viewer { get; } = new(ESparkDateTimeZoneKind.Viewer, null);
    public static SparkDateTimeZone Stored { get; } = new(ESparkDateTimeZoneKind.Stored, null);
    public static SparkDateTimeZone InZone(string ianaId) => new(ESparkDateTimeZoneKind.Zone, Require(ianaId, nameof(ianaId)));
    public static SparkDateTimeZone FromAttribute(string attributeName) => new(ESparkDateTimeZoneKind.ZoneFrom, Require(attributeName, nameof(attributeName)));

    private static string Require(string value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A zone or attribute name is required.", name) : value.Trim();
}

/// <summary>Reads and writes <see cref="SparkDateTimeZone"/> in its model-file form. Property names are matched case-insensitively.</summary>
public sealed class SparkDateTimeZoneJsonConverter : JsonConverter<SparkDateTimeZone>
{
    private const string ZoneProperty = "zone";
    private const string ZoneFromProperty = "zoneFrom";

    public override SparkDateTimeZone? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                var keyword = reader.GetString();
                if (string.Equals(keyword, "viewer", StringComparison.OrdinalIgnoreCase)) return SparkDateTimeZone.Viewer;
                if (string.Equals(keyword, "stored", StringComparison.OrdinalIgnoreCase)) return SparkDateTimeZone.Stored;
                throw new JsonException($"dateTimeDisplay: expected \"viewer\", \"stored\", {{ \"zone\": … }} or {{ \"zoneFrom\": … }}, got \"{keyword}\".");
            case JsonTokenType.StartObject:
                string? zone = null, zoneFrom = null;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                        throw new JsonException("dateTimeDisplay: malformed zone object.");
                    var name = reader.GetString();
                    reader.Read();
                    var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    reader.Skip(); // past a nested object or array; a no-op on a scalar
                    if (string.Equals(name, ZoneProperty, StringComparison.OrdinalIgnoreCase)) zone = value;
                    else if (string.Equals(name, ZoneFromProperty, StringComparison.OrdinalIgnoreCase)) zoneFrom = value;
                }
                if (!string.IsNullOrWhiteSpace(zone) && string.IsNullOrWhiteSpace(zoneFrom)) return SparkDateTimeZone.InZone(zone);
                if (!string.IsNullOrWhiteSpace(zoneFrom) && string.IsNullOrWhiteSpace(zone)) return SparkDateTimeZone.FromAttribute(zoneFrom);
                throw new JsonException("dateTimeDisplay: a zone object holds exactly one of \"zone\" and \"zoneFrom\".");
            default:
                throw new JsonException($"dateTimeDisplay: unexpected {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, SparkDateTimeZone value, JsonSerializerOptions options)
    {
        switch (value.Kind)
        {
            case ESparkDateTimeZoneKind.Viewer:
                writer.WriteStringValue("viewer");
                break;
            case ESparkDateTimeZoneKind.Stored:
                writer.WriteStringValue("stored");
                break;
            case ESparkDateTimeZoneKind.Zone:
                writer.WriteStartObject();
                writer.WriteString(ZoneProperty, value.Zone);
                writer.WriteEndObject();
                break;
            default:
                writer.WriteStartObject();
                writer.WriteString(ZoneFromProperty, value.ZoneFrom);
                writer.WriteEndObject();
                break;
        }
    }
}
