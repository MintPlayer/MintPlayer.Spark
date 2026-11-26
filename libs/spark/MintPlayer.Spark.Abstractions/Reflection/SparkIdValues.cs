using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace MintPlayer.Spark.Abstractions.Reflection;

/// <summary>
/// Moves ids between their typed form, <see cref="Id{T}"/>, and the canonical string the framework passes around
/// (#536 D23). The framework's own code is reflective: the entity mapper, row security, references and the query
/// executor read an entity's <c>Id</c> and its reference properties without knowing <c>T</c>. They go through here,
/// so they don't care whether a property is a <see cref="string"/> or an <see cref="Id{T}"/>.
/// </summary>
public static class SparkIdValues
{
    private static readonly ConcurrentDictionary<Type, Type?> Targets = new();
    private static readonly ConcurrentDictionary<Type, Func<string?, object>> Factories = new();

    /// <summary>
    /// The entity an id type names: <c>T</c> for <see cref="Id{T}"/> and <c>Id&lt;T&gt;?</c>, otherwise
    /// <see langword="null"/>.
    /// </summary>
    public static Type? TargetOf(Type type) => Targets.GetOrAdd(type, static t =>
    {
        var underlying = Nullable.GetUnderlyingType(t) ?? t;
        return underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(Id<>)
            ? underlying.GetGenericArguments()[0]
            : null;
    });

    /// <summary>Whether <paramref name="type"/> is <see cref="Id{T}"/> or <c>Id&lt;T&gt;?</c>.</summary>
    public static bool IsId(Type type) => TargetOf(type) is not null;

    /// <summary>
    /// The element type of a collection of ids (<c>List&lt;Id&lt;T&gt;&gt;</c>, <c>Id&lt;T&gt;[]</c>), or
    /// <see langword="null"/> when <paramref name="type"/> is not one.
    /// </summary>
    public static Type? CollectionElementOf(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return IsId(type.GetElementType()!) ? type.GetElementType() : null;
        var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        var element = enumerable?.GetGenericArguments()[0];
        return element is not null && IsId(element) ? element : null;
    }

    /// <summary>
    /// The canonical string of an id value: an <see cref="Id{T}"/> (boxed), a string, or anything else through
    /// <see cref="object.ToString"/>. The empty id and <see langword="null"/> both give <see langword="null"/>.
    /// </summary>
    public static string? ToText(object? value)
    {
        var text = value?.ToString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    /// The value to assign to a property of type <paramref name="propertyType"/> for the id
    /// <paramref name="text"/>: the string itself for a string property, an <see cref="Id{T}"/> (lowered, D7) for an
    /// id property, and <see langword="null"/> for an empty text on a nullable one.
    /// </summary>
    public static object? FromText(Type propertyType, string? text)
    {
        if (TargetOf(propertyType) is null)
            return text;
        if (string.IsNullOrEmpty(text) && Nullable.GetUnderlyingType(propertyType) is not null)
            return null;
        return Factories.GetOrAdd(Nullable.GetUnderlyingType(propertyType) ?? propertyType, static t =>
        {
            var method = t.GetMethod(nameof(Id<object>.FromTrusted), BindingFlags.Public | BindingFlags.Static)!;
            return s => method.Invoke(null, [s])!;
        })(text);
    }

    /// <summary>
    /// A collection of ids as their canonical strings, empty ids left out. Accepts any enumerable whose items are ids
    /// or strings; <see langword="null"/> gives an empty sequence.
    /// </summary>
    public static IEnumerable<string> ToTexts(object? values)
    {
        if (values is null or string) yield break;
        if (values is not IEnumerable items) yield break;
        foreach (var item in items)
            if (ToText(item) is { } text)
                yield return text;
    }

    /// <summary>
    /// The ids a reference property's value holds: its one id (a string or a boxed <see cref="Id{T}"/>), or each id of a
    /// collection. Anything else, <see langword="null"/> included, holds none.
    /// </summary>
    public static IEnumerable<string> ReferenceTexts(object? value)
    {
        switch (value)
        {
            case string or ISparkId:
                if (ToText(value) is { } single)
                    yield return single;
                yield break;
            case IEnumerable:
                foreach (var text in ToTexts(value))
                    yield return text;
                yield break;
        }
    }

    /// <summary>
    /// A collection of the shape <paramref name="collectionType"/> (a list, an array or an interface a list
    /// implements) filled with the ids in <paramref name="texts"/>.
    /// </summary>
    public static object ToCollection(Type collectionType, IEnumerable<string> texts)
    {
        var element = CollectionElementOf(collectionType)
            ?? throw new ArgumentException($"{collectionType} is not a collection of ids.", nameof(collectionType));
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
        foreach (var text in texts)
            list.Add(FromText(element, text));
        if (!collectionType.IsArray)
            return list;
        var array = Array.CreateInstance(element, list.Count);
        list.CopyTo(array, 0);
        return array;
    }

    /// <summary>The entity's id as its canonical string: its <c>Id</c> property, string or <see cref="Id{T}"/>.</summary>
    public static string? ReadEntityId(object entity)
    {
        var property = entity.GetType().GetCachedProperty("Id");
        return property is { CanRead: true } ? ToText(AccessorCache.GetGetter(property)(entity)) : null;
    }

    /// <summary>Writes <paramref name="id"/> into the entity's writable <c>Id</c> property, string or <see cref="Id{T}"/>.</summary>
    /// <returns>Whether the entity has such a property.</returns>
    public static bool WriteEntityId(object entity, string? id)
    {
        var property = entity.GetType().GetCachedProperty("Id");
        if (property is not { CanWrite: true } || (property.PropertyType != typeof(string) && !IsId(property.PropertyType)))
            return false;
        AccessorCache.GetSetter(property)(entity, FromText(property.PropertyType, id));
        return true;
    }
}
