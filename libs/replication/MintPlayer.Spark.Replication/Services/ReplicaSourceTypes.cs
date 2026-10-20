using System.Collections.Concurrent;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// The owner's side of a source collection name (warehouse §4.2): the CLR type of this module's persistent-object type
/// stored in it. Only types of the composed model qualify, so a manifest can never name a collection the model does not
/// govern (and so cannot be authorized).
/// </summary>
internal interface IReplicaSourceTypes
{
    /// <summary>The model's CLR type stored in <paramref name="collection"/>, or null.</summary>
    Type? Resolve(string collection);

    /// <summary>The source collection a document of <paramref name="entityType"/> is stored in.</summary>
    string CollectionOf(Type entityType);
}

internal sealed partial class ReplicaSourceTypes : IReplicaSourceTypes
{
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IDocumentStore documentStore;

    private readonly ConcurrentDictionary<string, Type?> byCollection = new(StringComparer.Ordinal);

    public Type? Resolve(string collection)
        => byCollection.GetOrAdd(collection, name => modelLoader.GetEntityTypes()
            .Select(definition => FindType(definition.ClrType))
            .FirstOrDefault(type => type is not null && string.Equals(documentStore.Conventions.FindCollectionName(type), name, StringComparison.Ordinal)));

    public string CollectionOf(Type entityType) => documentStore.Conventions.FindCollectionName(entityType);

    private static Type? FindType(string? clrType)
    {
        if (string.IsNullOrEmpty(clrType))
            return null;
        return Type.GetType(clrType, throwOnError: false)
            ?? AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a => a.GetType(clrType, throwOnError: false))
                .FirstOrDefault(t => t is not null);
    }
}
