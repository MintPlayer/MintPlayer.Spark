using System.Drawing;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using MintPlayer.Spark.Abstractions.Reflection;

namespace MintPlayer.Spark.Storage;

/// <summary>
/// How a document is serialized by the in-memory store, and by the relational providers' revisions (#536 S8). It follows the RavenDB client's conventions, so an entity reads back
/// the same from either provider: Newtonsoft.Json, enums as their names, <see cref="Color"/> as <c>#rrggbb</c> (Spark's
/// RavenDB converter), and collections replaced rather than appended to on read. Each load deserializes a fresh
/// instance, which is what keeps sessions from sharing state.
/// </summary>
internal static class SparkDocumentJson
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        Converters = { new StringEnumConverter(), new ColorConverter(), new IdConverter() },
        ObjectCreationHandling = ObjectCreationHandling.Replace,
        NullValueHandling = NullValueHandling.Include,
        DateParseHandling = DateParseHandling.None,
        ReferenceLoopHandling = ReferenceLoopHandling.Error,
    };

    public static string Serialize(object entity) => JsonConvert.SerializeObject(entity, Settings);

    public static object Deserialize(string json, Type type) => JsonConvert.DeserializeObject(json, type, Settings)!;

    /// <summary><see cref="Abstractions.Id{T}"/> as its canonical string, as RavenDB stores it (#536 D23).</summary>
    private sealed class IdConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => SparkIdValues.IsId(objectType);

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
            => SparkIdValues.FromText(objectType, reader.TokenType == JsonToken.String ? (string?)reader.Value : null);

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (SparkIdValues.ToText(value) is { } text) writer.WriteValue(text);
            else writer.WriteNull();
        }
    }

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
