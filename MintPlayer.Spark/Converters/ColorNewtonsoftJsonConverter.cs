using Newtonsoft.Json;
using System.Drawing;

namespace MintPlayer.Spark.Converters;

/// <summary>
/// Newtonsoft.Json converter for System.Drawing.Color that serializes as hex string "#rrggbb".
/// Used by RavenDB for document storage.
/// </summary>
internal sealed class ColorNewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter<Color>
{
    public override Color ReadJson(JsonReader reader, Type objectType, Color existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null || reader.Value == null)
            return Color.Empty;

        var hex = reader.Value.ToString()!;
        return ColorTranslator.FromHtml(hex);
    }

    public override void WriteJson(JsonWriter writer, Color value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value.IsEmpty)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue($"#{value.R:x2}{value.G:x2}{value.B:x2}");
    }
}
