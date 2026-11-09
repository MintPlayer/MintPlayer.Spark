using MintPlayer.Spark.Abstractions.Reflection;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Services;

/// <summary>What kind of RavenDB index a type is, and therefore what Spark may conclude about it.</summary>
public enum RavenIndexKind
{
    /// <summary>Not a RavenDB index type at all.</summary>
    None,

    /// <summary>
    /// <c>AbstractIndexCreationTask&lt;TDocument&gt;</c> or
    /// <c>AbstractIndexCreationTask&lt;TDocument, TReduceResult&gt;</c> — exactly one mapped collection.
    /// </summary>
    Map,

    /// <summary>
    /// A multi-map index. It maps <em>several</em> collections and names none of them in its type
    /// arguments, so no collection type is derivable.
    /// </summary>
    MultiMap,

    /// <summary>
    /// An index Spark cannot introspect: <c>AbstractGenericIndexCreationTask&lt;T&gt;</c>, the
    /// non-generic <c>AbstractIndexCreationTask</c>, or a JavaScript index. RavenDB deploys it; Spark
    /// simply has no collection to attribute it to.
    /// </summary>
    Opaque,
}

/// <summary>
/// The one place that reads RavenDB's index base-type hierarchy. Four near-duplicate walks used to do
/// this, with three different acceptance rules and three different wrong answers.
/// </summary>
/// <remarks>
/// ⚠️ <b>The hierarchy is not what it looks like, and the obvious reading is wrong in both directions.</b>
/// Measured by reflection over RavenDB.Client 7.2.6:
/// <code>
/// AbstractIndexCreationTask&lt;TDocument&gt;                -> AbstractIndexCreationTask&lt;TDocument, TDocument&gt;
/// AbstractIndexCreationTask&lt;TDocument,TReduceResult&gt;  -> AbstractGenericIndexCreationTask&lt;TReduceResult&gt;
/// AbstractMultiMapIndexCreationTask&lt;TReduceResult&gt;    -> AbstractGenericIndexCreationTask&lt;TReduceResult&gt;
/// AbstractMultiMapIndexCreationTask                        -> AbstractMultiMapIndexCreationTask&lt;object&gt;
/// </code>
/// So the two-argument form does <b>not</b> derive from the one-argument form — the derivation runs the
/// other way — and a multi-map's single type argument is the <b>reduce result</b>, never a collection.
/// Reading <c>TypeArguments[0]</c> off a multi-map yields a type that is not the mapped collection, and
/// off the non-generic multi-map yields <see cref="object"/>.
/// <para>
/// ⚠️ <b>The walk must stay a loop over base types.</b> A direct test on <c>BaseType</c> breaks the
/// moment anything is interposed between the user's index and RavenDB's — which is exactly what a
/// Spark base class does.
/// </para>
/// <para>
/// ⚠️ Keep in sync with the analyzer-side twin at
/// <c>libs/source_generators/MintPlayer.Spark.SourceGenerators/Naming/RavenIndexHierarchy.cs</c>. They
/// cannot be one file: this side speaks <see cref="Type"/> on net10.0, that side speaks
/// <c>INamedTypeSymbol</c> on netstandard2.0 and must not reference RavenDB.Client at all.
/// </para>
/// </remarks>
internal static class RavenIndexHierarchy
{
    /// <summary>
    /// RavenDB's own criterion for "this type is an index", and therefore the set
    /// <c>IndexCreation.CreateIndexes</c> will actually deploy.
    /// </summary>
    /// <remarks>
    /// Discovery must use exactly this, or Spark's catalog and RavenDB's deployment disagree. They did:
    /// a two-argument map-reduce index failed an open-generic identity test, was never discovered, and
    /// so was never registered — while RavenDB deployed it anyway. A query naming it then failed with
    /// "no deployed index has that name", which was false and pointed the author at the wrong thing.
    /// </remarks>
    internal static bool IsIndex(Type type) => typeof(AbstractIndexCreationTask).IsAssignableFrom(type);

    /// <summary>Classifies an index type and, for <see cref="RavenIndexKind.Map"/>, its collection.</summary>
    internal static RavenIndexKind Classify(Type indexType, out Type? collectionType)
    {
        var resolved = ReflectionCache.GetOrAdd<(string Op, Type Type), (RavenIndexKind Kind, Type? Collection)>(
            ("RavenIndexHierarchy.Classify", indexType),
            static k => Resolve(k.Type));

        collectionType = resolved.Collection;
        return resolved.Kind;
    }

    /// <summary>The mapped collection, or <see langword="null"/> when the index names none.</summary>
    internal static Type? MappedCollection(Type indexType)
        => Classify(indexType, out var collection) == RavenIndexKind.Map ? collection : null;

    private static (RavenIndexKind Kind, Type? Collection) Resolve(Type indexType)
    {
        if (!IsIndex(indexType)) return (RavenIndexKind.None, null);

        for (var current = indexType; current is not null && current != typeof(object); current = current.BaseType)
        {
            if (!current.IsGenericType) continue;

            var definition = current.GetGenericTypeDefinition();

            // Multi-map FIRST. A multi-map also derives from AbstractGenericIndexCreationTask<T>, so a
            // later rule would otherwise claim it and hand back the reduce result as a collection.
            if (definition == typeof(AbstractMultiMapIndexCreationTask<>))
                return (RavenIndexKind.MultiMap, null);

            // Arity 1 and arity 2 both name the document as their FIRST argument. The one-argument
            // form is literally AbstractIndexCreationTask<T, T>, so the walk reaches the same answer
            // by either route.
            if (definition == typeof(AbstractIndexCreationTask<>) ||
                definition == typeof(AbstractIndexCreationTask<,>))
                return (RavenIndexKind.Map, current.GetGenericArguments()[0]);
        }

        // It is an index — RavenDB will deploy it — but nothing in its hierarchy names a collection.
        return (RavenIndexKind.Opaque, null);
    }
}
