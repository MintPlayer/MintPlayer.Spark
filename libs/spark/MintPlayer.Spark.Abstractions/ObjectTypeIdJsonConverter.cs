using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Reads <see cref="PersistentObject.ObjectTypeId"/> from the wire without throwing on a value that
/// is not a GUID.
/// <para>
/// Clients address an entity type by <em>alias</em> as readily as by id — <c>/po/car/new</c> — and
/// echoing that alias back in the body is the obvious thing for a client to do. With the default
/// binder that is fatal: deserialization throws before the handler runs, so the caller gets a bare
/// 500 with no indication of which field was wrong, and no endpoint code ever executes.
/// </para>
/// <para>
/// Answering <see cref="Guid.Empty"/> instead is safe precisely because <b>no endpoint trusts this
/// field</b>. Create, Update, Refresh and ExecuteCustomAction all resolve the entity type from the
/// route and overwrite it — deliberately, so that a client cannot reach one collection through
/// another's permissions. The value is therefore advisory on the way in and authoritative only on
/// the way out, and tolerating a bad one costs nothing.
/// </para>
/// </summary>
internal sealed class ObjectTypeIdJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return Guid.Empty;

        if (reader.TokenType == JsonTokenType.String)
            return reader.TryGetGuid(out var guid) ? guid : Guid.Empty;

        return Guid.Empty;
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
