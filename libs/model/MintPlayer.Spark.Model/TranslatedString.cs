using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

[JsonConverter(typeof(TranslatedStringJsonConverter))]
public class TranslatedString
{
    public Dictionary<string, string> Translations { get; set; } = new();

    /// <summary>
    /// A translation KEY, set when the JSON held a plain string instead of an object (#467, D1/D4):
    /// App_Data files refer to <c>translations.json</c> by key and never embed translated text.
    /// The loader resolves such a value through <c>SparkText</c> (MintPlayer.Spark.Abstractions) into one carrying
    /// <see cref="Translations"/>; an unresolved one serializes back to its key.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>A reference to a <c>translations.json</c> key, not yet resolved.</summary>
    public static TranslatedString FromKey(string key) => new() { Key = key };

    public string GetValue(string culture)
    {
        if (Translations.TryGetValue(culture, out var value))
            return value;

        // Fallback: try base culture (e.g., "en" from "en-US")
        var baseCulture = culture.Split('-')[0];
        if (Translations.TryGetValue(baseCulture, out value))
            return value;

        // Fallback: return first available or empty
        return Translations.Values.FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// Returns the first available translation value (for programmatic use where a plain string is needed).
    /// </summary>
    public string GetDefaultValue()
    {
        return Translations.Values.FirstOrDefault() ?? string.Empty;
    }

    public static TranslatedString Create(string en, string? fr = null, string? nl = null)
    {
        var ts = new TranslatedString();
        ts.Translations["en"] = en;
        if (fr != null) ts.Translations["fr"] = fr;
        if (nl != null) ts.Translations["nl"] = nl;
        return ts;
    }
}

/// <summary>
/// Serializes TranslatedString as a flat JSON object: {"en": "value", "fr": "value"}.
/// A JSON string is a translation key (<see cref="TranslatedString.Key"/>); an unresolved key
/// (no translations) is written back as that string, the form App_Data files use.
/// </summary>
public class TranslatedStringJsonConverter : JsonConverter<TranslatedString>
{
    public override TranslatedString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
            return TranslatedString.FromKey(reader.GetString()!);

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a translation key or an object for TranslatedString");

        var ts = new TranslatedString();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return ts;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var language = reader.GetString()!;
                reader.Read();
                var value = reader.GetString();
                if (value != null)
                    ts.Translations[language] = value;
            }
        }

        throw new JsonException("Unexpected end of JSON for TranslatedString");
    }

    public override void Write(Utf8JsonWriter writer, TranslatedString value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Key is not null && value.Translations.Count == 0)
        {
            writer.WriteStringValue(value.Key);
            return;
        }

        writer.WriteStartObject();
        foreach (var kvp in value.Translations)
        {
            writer.WriteString(kvp.Key, kvp.Value);
        }
        writer.WriteEndObject();
    }
}
