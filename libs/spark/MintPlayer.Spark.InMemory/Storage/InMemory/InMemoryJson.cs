using System.Drawing;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// How the in-memory store serializes a document. It follows the RavenDB client's conventions, so an entity reads back
/// the same from either provider: Newtonsoft.Json, enums as their names, <see cref="Color"/> as <c>#rrggbb</c> (Spark's
/// RavenDB converter), and collections replaced rather than appended to on read. Each load deserializes a fresh
/// instance, which is what keeps sessions from sharing state.
/// </summary>
internal static class InMemoryJson
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        Converters = { new StringEnumConverter(), new ColorConverter() },
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.None,
        ReferenceLoopHandling = ReferenceLoopHandling.Error,
    };

    public static string Serialize(object entity) => JsonConvert.SerializeObject(entity, Settings);

    public static object Deserialize(string json, Type type) => JsonConvert.DeserializeObject(json, type, Settings)!;

    private sealed class ColorConverter : JsonConverter<Color>
    {
        public override Color ReadJson(JsonReader reader, Type objectType, Color existingValue, bool hasExistingValue, JsonSerializer serializer)
            => reader.TokenType == JsonToken.Null || reader.Value is null ? Color.Empty : ColorTranslator.FromHtml(reader.Value.ToString()!);

        public override void WriteJson(JsonWriter writer, Color value, JsonSerializer serializer)
        {
            if (value.IsEmpty)
                writer.WriteNull();
            else
                writer.WriteValue($"#{value.R:x2}{value.G:x2}{value.B:x2}");
        }
    }
}
