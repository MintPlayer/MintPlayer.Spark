using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

[JsonConverter(typeof(TranslatedStringJsonConverter))]
public class TranslatedString
{
    public Dictionary<string, string> Translations { get; set; } = new();

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
/// Serializes TranslatedString as a flat JSON object: {"en": "value", "fr": "value"}
/// </summary>
public class TranslatedStringJsonConverter : JsonConverter<TranslatedString>
{
    public override TranslatedString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject token for TranslatedString");

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

        writer.WriteStartObject();
        foreach (var kvp in value.Translations)
        {
            writer.WriteString(kvp.Key, kvp.Value);
        }
        writer.WriteEndObject();
    }
}
