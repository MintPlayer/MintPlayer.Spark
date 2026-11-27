using System.Collections.Concurrent;
using System.Reflection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Storage.InMemory;

/// <summary>
/// Collection names and document ids, by the rules every Spark store follows: an <see cref="IHasNaturalId"/> entity
/// is stored under the id it derives, any other under <c>{Collection}/{Guid}</c> (as <c>SparkDocumentStoreConventions</c>
/// does for RavenDB). A collection is <see cref="SparkCollectionNames.Of"/>.
/// </summary>
internal static class InMemoryConventions
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> IdProperties = new();

    public static string GetCollectionName(Type type) => SparkCollectionNames.Of(type);

    /// <summary>A new id, lower case (#536 D7), as RavenDB's conventions make it.</summary>
    public static string NewId(object entity)
        => (entity is IHasNaturalId natural ? natural.GetId() : $"{GetCollectionName(entity.GetType())}/{Guid.NewGuid()}").ToLowerInvariant();

    /// <summary>The entity's <c>Id</c> property, a writable <see cref="string"/> or <see cref="Id{T}"/> one.</summary>
    public static string? ReadId(object entity) => IdProperty(entity.GetType()) is { } property
        ? SparkIdValues.ToText(property.GetValue(entity))
        : null;

    public static void WriteId(object entity, string id)
    {
        if (IdProperty(entity.GetType()) is { } property)
            property.SetValue(entity, SparkIdValues.FromText(property.PropertyType, id));
    }

    private static PropertyInfo? IdProperty(Type type) => IdProperties.GetOrAdd(type, static t =>
        t.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var p, CanWrite: true } property
            && (p == typeof(string) || SparkIdValues.IsId(p))
            ? property
            : null);
}
