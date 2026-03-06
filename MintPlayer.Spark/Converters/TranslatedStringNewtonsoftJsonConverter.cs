using MintPlayer.Spark.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MintPlayer.Spark.Converters;

/// <summary>
/// Newtonsoft.Json converter for TranslatedString that serializes as a flat JSON object: {"en": "value", "nl": "value"}.
/// Required because aspnet-prerendering uses Newtonsoft for SSR data serialization.
/// </summary>
internal sealed class TranslatedStringNewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter<TranslatedString>
{
    public override TranslatedString? ReadJson(JsonReader reader, Type objectType, TranslatedString? existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var obj = JObject.Load(reader);
        var ts = new TranslatedString();
        foreach (var prop in obj.Properties())
        {
            if (prop.Value.Type == JTokenType.String)
            {
                ts.Translations[prop.Name] = prop.Value.Value<string>()!;
            }
        }
        return ts;
    }

    public override void WriteJson(JsonWriter writer, TranslatedString? value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();
        foreach (var kvp in value.Translations)
        {
            writer.WritePropertyName(kvp.Key);
            writer.WriteValue(kvp.Value);
        }
        writer.WriteEndObject();
    }
}
