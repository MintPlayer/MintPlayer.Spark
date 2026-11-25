using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Relational.Model;

/// <summary>
/// Converters between an id property and its key column (#536 D23 rule 8): the canonical <c>"people/{key}"</c> on the
/// CLR side, the key alone in the column (a Guid, a bigint or the natural key's text). A text that is not an id of the
/// target collection fails the write instead of storing a wrong key.
/// </summary>
internal static class SparkIdConverters
{
    /// <summary>
    /// The converter for a property of type <paramref name="propertyType"/> (an <see cref="Id{T}"/>, <see cref="Id{T}"/>?
    /// or a string) that holds ids of <paramref name="target"/>; <see langword="null"/> when the column holds the text as is.
    /// </summary>
    public static ValueConverter? For(Type propertyType, SparkTable target)
    {
        var clr = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (target.Key == SparkKeyShape.Text)
            return clr == typeof(string) ? null : Create(typeof(TextIdConverter<>), clr, target.Prefix);
        var definition = target.Key switch
        {
            SparkKeyShape.Guid => clr == typeof(string) ? typeof(GuidTextConverter) : typeof(GuidIdConverter<>),
            SparkKeyShape.Integer => clr == typeof(string) ? typeof(IntegerTextConverter) : typeof(IntegerIdConverter<>),
            _ => clr == typeof(string) ? typeof(NaturalTextConverter) : typeof(NaturalIdConverter<>),
        };
        return Create(definition, clr, target.Prefix);
    }

    /// <summary>The converter for an id property that points at a type no table stores: the whole id as text.</summary>
    public static ValueConverter? ForUnstored(Type propertyType)
    {
        var clr = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        return clr == typeof(string) ? null : Create(typeof(TextIdConverter<>), clr, "");
    }

    private static ValueConverter Create(Type definition, Type clr, string prefix)
    {
        var type = definition.IsGenericTypeDefinition ? definition.MakeGenericType(clr.GetGenericArguments()[0]) : definition;
        return (ValueConverter)Activator.CreateInstance(type, prefix)!;
    }

    public static string Key(string? text, string prefix)
    {
        var lowered = (text ?? "").ToLowerInvariant();
        if (!lowered.StartsWith(prefix, StringComparison.Ordinal) || lowered.Length == prefix.Length)
            throw new FormatException($"'{text}' is not an id of collection '{prefix.TrimEnd('/')}'.");
        return lowered[prefix.Length..];
    }

    public static Guid GuidKey(string? text, string prefix) => Guid.Parse(Key(text, prefix));

    public static long IntegerKey(string? text, string prefix)
        => long.Parse(Key(text, prefix), NumberStyles.None, CultureInfo.InvariantCulture);

    public static string GuidText(Guid key, string prefix) => prefix + key.ToString("N");

    public static string IntegerText(long key, string prefix) => prefix + key.ToString(CultureInfo.InvariantCulture);

    public static string Lower(string? text) => (text ?? "").ToLowerInvariant();

    private sealed class GuidIdConverter<T>(string prefix) : ValueConverter<Id<T>, Guid>(
        id => GuidKey(id.Value, prefix), key => Id<T>.FromTrusted(GuidText(key, prefix)));

    private sealed class IntegerIdConverter<T>(string prefix) : ValueConverter<Id<T>, long>(
        id => IntegerKey(id.Value, prefix), key => Id<T>.FromTrusted(IntegerText(key, prefix)));

    private sealed class NaturalIdConverter<T>(string prefix) : ValueConverter<Id<T>, string>(
        id => Key(id.Value, prefix), key => Id<T>.FromTrusted(prefix + key));

#pragma warning disable CS9113 // the prefix is part of every converter's constructor; whole-text ids don't need it
    private sealed class TextIdConverter<T>(string prefix) : ValueConverter<Id<T>, string>(
        id => Lower(id.Value), text => Id<T>.FromTrusted(text));
#pragma warning restore CS9113

    private sealed class GuidTextConverter(string prefix) : ValueConverter<string, Guid>(
        text => GuidKey(text, prefix), key => GuidText(key, prefix));

    private sealed class IntegerTextConverter(string prefix) : ValueConverter<string, long>(
        text => IntegerKey(text, prefix), key => IntegerText(key, prefix));

    private sealed class NaturalTextConverter(string prefix) : ValueConverter<string, string>(
        text => Key(text, prefix), key => prefix + key);
}
