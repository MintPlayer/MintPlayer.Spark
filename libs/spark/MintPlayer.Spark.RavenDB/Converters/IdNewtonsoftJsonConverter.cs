using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Raven.Client.Json.Serialization.NewtonsoftJson;
using System.Reflection;

namespace MintPlayer.Spark.Converters;

/// <summary>
/// Newtonsoft (RavenDB documents) for <see cref="Id{T}"/>: a reference is stored as its canonical string, exactly
/// as a <see cref="string"/> reference was, so the documents don't change (#536 D23 rule 6). The empty id is
/// <c>null</c>.
/// </summary>
public sealed class IdNewtonsoftJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => SparkIdValues.IsId(objectType);

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        => reader.TokenType switch
        {
            JsonToken.Null => SparkIdValues.FromText(objectType, null),
            JsonToken.String => SparkIdValues.FromText(objectType, (string?)reader.Value),
            _ => throw new JsonSerializationException($"An {objectType} is stored as a string."),
        };

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (SparkIdValues.ToText(value) is { } text) writer.WriteValue(text);
        else writer.WriteNull();
    }
}

/// <summary>
/// RavenDB's contract resolver, minus an entity's own <c>Id&lt;T&gt;</c> when the entity has the generated
/// <c>__DocumentId</c> identity member: the id is the document's key, never a field in its body (D23 rule 6), so
/// re-saving a document written with a <see cref="string"/> id leaves it byte-identical.
/// </summary>
internal sealed class SparkRavenContractResolver(Raven.Client.Json.Serialization.ISerializationConventions conventions)
    : DefaultRavenContractResolver(conventions)
{
    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);
        if (member.Name == "Id" && property.PropertyType is { } type && SparkIdValues.TargetOf(type) == member.DeclaringType
            && SparkDocumentIdentity.Of(member.DeclaringType!) is not null)
            property.Ignored = true;
        return property;
    }
}

/// <summary>The generated identity member of an entity with an <c>Id&lt;T&gt;</c> (D23 rule 6).</summary>
public static class SparkDocumentIdentity
{
    /// <summary>The member's name, as the generator emits it.</summary>
    public const string MemberName = "__DocumentId";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo?> Members = new();

    /// <summary>The <c>__DocumentId</c> property of <paramref name="type"/> or a base type, or <see langword="null"/>.</summary>
    public static PropertyInfo? Of(Type type) => Members.GetOrAdd(type, static t =>
    {
        for (var current = t; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(MemberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property is not null && property.PropertyType == typeof(string))
                return property;
        }
        return null;
    });
}
