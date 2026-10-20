using System.Globalization;
using MintPlayer.Spark.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MintPlayer.Spark.Converters;

/// <summary>
/// Newtonsoft.Json converter for <see cref="TranslatedString"/>, used by RavenDB for document storage.
/// <para>
/// It writes exactly what <see cref="TranslatedStringJsonConverter"/> writes on the API —
/// <c>{"Entries":[{"Tag":"en","Value":"…"}]}</c> — through the shared <see cref="TranslatedStringJson"/>
/// codec, so a stored document and an API payload are one shape (commerce X17). Without it RavenDB
/// would persist the POCO, which is how the old <c>{"Translations":{…},"Key":null}</c> form arose.
/// </para>
/// <para>
/// A <see cref="TranslatedString.Key"/> is an App_Data reference and is never stored: a key-only value
/// is written as null.
/// </para>
/// <para>
/// The pre-X17 form is not read (no backward compatibility, K3): the data migrations rewrite it
/// (IdentityProvider <c>M_202610191200_OidcResourceTextEntries</c>, Fleet
/// <c>M_202610191205_TranslatedStringEntries</c>). No collection holding the type has revisions
/// enabled, so no immutable copy of the old form remains to be read.
/// </para>
/// </summary>
internal sealed class TranslatedStringNewtonsoftJsonConverter : Newtonsoft.Json.JsonConverter<TranslatedString>
{
    public override TranslatedString? ReadJson(JsonReader reader, Type objectType, TranslatedString? existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        var token = JToken.Load(reader);
        if (token is not JObject obj)
            throw new JsonSerializationException($"Expected a TranslatedString object, got {token.Type}");

        var ts = new TranslatedString();
        foreach (var property in obj.Properties())
        {
            if (string.Equals(property.Name, TranslatedStringJson.EntriesName, StringComparison.OrdinalIgnoreCase) && property.Value is JArray entries)
            {
                foreach (var item in entries)
                {
                    if (item is not JObject entry)
                        throw new JsonSerializationException("A TranslatedString entry must be an object");
                    var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var field in entry.Properties())
                        fields[field.Name] = TextOf(field.Value);
                    if (Convert(() => TranslatedStringJson.EntryFrom(fields)) is { } parsed) ts.Entries.Add(parsed);
                }
            }
            else if (property.Value.Type is JTokenType.String && TextOf(property.Value) is { } text)
            {
                ts.Entries.Add(Convert(() => TranslatedStringJson.PlainEntry(property.Name, text)));
            }
            else if (property.Value.Type is not JTokenType.Null)
            {
                throw new JsonSerializationException(
                    $"TranslatedString member '{property.Name}' must be \"Entries\": [...] or a language tag with a text");
            }
        }
        return ts;
    }

    public override void WriteJson(JsonWriter writer, TranslatedString? value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value is null || (value.Key is not null && value.Entries.Count == 0))
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName(TranslatedStringJson.EntriesName);
        writer.WriteStartArray();
        foreach (var entry in value.Entries)
        {
            writer.WriteStartObject();
            foreach (var (field, text) in TranslatedStringJson.FieldsOf(entry))
            {
                writer.WritePropertyName(field);
                writer.WriteValue(text);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// The member as a string. A reader that parses dates hands <c>ValidFrom</c>/<c>ValidTo</c> over as a
    /// <see cref="DateTime"/>; it is turned back into the stored <c>yyyy-MM-dd</c>.
    /// </summary>
    private static string? TextOf(JToken token) => token.Type switch
    {
        JTokenType.Null or JTokenType.Undefined => null,
        JTokenType.Date when token.ToObject<object>() is DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        JTokenType.Date when token.ToObject<object>() is DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => token.Value<string>(),
    };

    private static T Convert<T>(Func<T> parse)
    {
        try { return parse(); }
        catch (System.Text.Json.JsonException ex) { throw new JsonSerializationException(ex.Message, ex); }
    }
}
