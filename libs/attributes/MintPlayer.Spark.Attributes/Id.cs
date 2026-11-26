using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// The id of an entity of type <typeparamref name="T"/>: the canonical string <c>"{collection}/{key}"</c>,
/// lower case, the same on every storage provider (#536 D23, D7).
/// <para>
/// There is no public constructor. An id is canonicalised once, through the app model:
/// <c>ids.NewAsync&lt;T&gt;()</c> for a new entity, <c>ids.Parse&lt;T&gt;(text)</c> for text from outside, and the
/// framework's JSON converters and route binder for requests. <c>default</c> is the empty id, and a store
/// refuses an entity whose id is empty.
/// </para>
/// <para>
/// Equality, hashing, ordering and <see cref="ToString"/> are ordinal operations on the canonical string.
/// Ordering is therefore only meaningful per provider: numeric ids sort as text on RavenDB, and Guid ids
/// have no meaningful order anywhere.
/// </para>
/// </summary>
/// <typeparam name="T">The entity the id belongs to. Ids of different entities don't compare.</typeparam>
[JsonConverter(typeof(IdJsonConverterFactory))]
public readonly struct Id<T> : IEquatable<Id<T>>, IComparable<Id<T>>, IComparable, ISparkId
{
    private readonly string? value;

    private Id(string value) => this.value = value;

    /// <summary>The canonical string, <c>"{collection}/{key}"</c>; empty for the empty id.</summary>
    public string Value => value ?? string.Empty;

    /// <summary>True for <c>default</c>, the id of an entity that has none yet.</summary>
    public bool IsEmpty => value is null;

    /// <summary>The collection part, everything before the first <c>/</c>.</summary>
    public string Collection
    {
        get
        {
            if (value is null) return string.Empty;
            var slash = value.IndexOf('/');
            return slash < 0 ? value : value[..slash];
        }
    }

    /// <summary>The key, everything after the first <c>/</c>. A hierarchical natural key may itself contain <c>/</c>.</summary>
    public string Key
    {
        get
        {
            if (value is null) return string.Empty;
            var slash = value.IndexOf('/');
            return slash < 0 ? string.Empty : value[(slash + 1)..];
        }
    }

    /// <summary>
    /// Wraps a string that is already a canonical id of <typeparamref name="T"/>, lowering it (D7). For the
    /// framework, its source generators and the storage providers, which read ids the store handed back.
    /// Application code uses <c>ids.Parse&lt;T&gt;</c>, which also checks the collection and normalises the key.
    /// </summary>
    /// <returns>The id, or the empty id for <see langword="null"/> or an empty string.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Id<T> FromTrusted(string? value)
        => string.IsNullOrEmpty(value) ? default : new Id<T>(value.ToLowerInvariant());

    public bool Equals(Id<T> other) => string.Equals(value, other.value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Id<T> other && Equals(other);

    public override int GetHashCode() => value is null ? 0 : StringComparer.Ordinal.GetHashCode(value);

    public int CompareTo(Id<T> other) => string.CompareOrdinal(value, other.value);

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        Id<T> other => CompareTo(other),
        _ => throw new ArgumentException($"Object must be of type {typeof(Id<T>)}.", nameof(obj)),
    };

    public static bool operator ==(Id<T> left, Id<T> right) => left.Equals(right);

    public static bool operator !=(Id<T> left, Id<T> right) => !left.Equals(right);

    public static bool operator <(Id<T> left, Id<T> right) => left.CompareTo(right) < 0;

    public static bool operator >(Id<T> left, Id<T> right) => left.CompareTo(right) > 0;

    public static bool operator <=(Id<T> left, Id<T> right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Id<T> left, Id<T> right) => left.CompareTo(right) >= 0;

    /// <summary>The canonical string; empty for the empty id.</summary>
    public override string ToString() => Value;
}

/// <summary>
/// Any <see cref="Id{T}"/>, for code that handles ids without knowing <c>T</c>: a provider's query value converter
/// matches a boxed id by this interface.
/// </summary>
public interface ISparkId
{
    /// <summary>The canonical string; empty for the empty id.</summary>
    string Value { get; }

    /// <summary>True for the empty id.</summary>
    bool IsEmpty { get; }
}

/// <summary>
/// How new ids of an entity are made (#536 D23 rule 2). The attribute <see cref="IdKindAttribute"/> gives the
/// default, and the app model may override it, also for entities shipped in feature packages.
/// </summary>
public enum SparkIdKind
{
    /// <summary>A random (v4) Guid, written as 32 hex digits. The default: it can't be counted or guessed.</summary>
    Guid = 0,

    /// <summary>A number from the provider's HiLo generator. On RavenDB the key keeps its node tag (<c>1-a</c>).</summary>
    Integer = 1,

    /// <summary>The key comes from the entity's own business key (<c>IHasNaturalId</c> or an id template).</summary>
    Natural = 2,
}

/// <summary>
/// System.Text.Json for every <see cref="Id{T}"/>: a JSON string, <c>null</c> for the empty id (#536 D23 rule 9).
/// Reading lowers the text (D7); the web layer checks the collection through the app model where it binds an id.
/// </summary>
public sealed class IdJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Id<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(IdJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class IdJsonConverter<T> : JsonConverter<Id<T>>
    {
        public override bool HandleNull => true;

        public override Id<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.Null => default,
            JsonTokenType.String => Id<T>.FromTrusted(reader.GetString()),
            _ => throw new JsonException($"An {typeof(Id<T>)} is a JSON string."),
        };

        public override void Write(Utf8JsonWriter writer, Id<T> value, JsonSerializerOptions options)
        {
            if (value.IsEmpty) writer.WriteNullValue();
            else writer.WriteStringValue(value.Value);
        }
    }
}

/// <summary>Sets how new ids of this entity are made. Without it, ids are Guids.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IdKindAttribute : Attribute
{
    public IdKindAttribute(SparkIdKind kind) => Kind = kind;

    public SparkIdKind Kind { get; }
}
