using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Naming;

/// <summary>What kind of RavenDB index a type is, and therefore what may be concluded about it.</summary>
internal enum RavenIndexKind
{
    /// <summary>Not a RavenDB index type at all.</summary>
    None,

    /// <summary>
    /// <c>AbstractIndexCreationTask&lt;TDocument&gt;</c> or
    /// <c>AbstractIndexCreationTask&lt;TDocument, TReduceResult&gt;</c> — exactly one mapped collection.
    /// </summary>
    Map,

    /// <summary>A multi-map index: several collections, none named in its type arguments.</summary>
    MultiMap,

    /// <summary>An index with no derivable collection — generic base, non-generic base, or JavaScript.</summary>
    Opaque,
}

/// <summary>
/// The analyzer/generator-side reading of RavenDB's index base-type hierarchy.
/// </summary>
/// <remarks>
/// ⚠️ <b>Keep in sync with the runtime twin at
/// <c>libs/spark/MintPlayer.Spark/Services/RavenIndexHierarchy.cs</c>.</b> They cannot be one file:
/// this assembly targets netstandard2.0 and <b>must not</b> reference RavenDB.Client — a Roslyn
/// analyzer ships without dependencies, which is why the generator already probes for Raven types by
/// metadata name rather than by <c>typeof</c>. So this side matches on names and namespaces, and the
/// runtime side matches on real <c>Type</c> identity, and the two must agree by discipline.
/// <para>
/// ⚠️ <b>The hierarchy is not what it looks like.</b> Measured over RavenDB.Client 7.2.6:
/// <c>AbstractIndexCreationTask&lt;TDocument, TReduceResult&gt;</c> derives from
/// <c>AbstractGenericIndexCreationTask&lt;TReduceResult&gt;</c> — <b>not</b> from the one-argument form,
/// which itself derives from <c>AbstractIndexCreationTask&lt;TDocument, TDocument&gt;</c>. And
/// <c>AbstractMultiMapIndexCreationTask&lt;T&gt;</c>'s argument is the <b>reduce result</b>, never a
/// collection. A doc comment in this repo asserted the opposite for both, which is where the matching
/// test stub's fictional hierarchy came from.
/// </para>
/// <para>
/// ⚠️ <b>Start the walk at the type itself, not at <c>BaseType</c>, and keep it a loop.</b> Anything
/// interposed between a user's index and RavenDB's — a Spark base class, for instance — breaks a
/// single-step check.
/// </para>
/// </remarks>
internal static class RavenIndexHierarchy
{
    internal const string RavenIndexesNamespace = "Raven.Client.Documents.Indexes";

    private const string AbstractIndexCreationTaskName = "AbstractIndexCreationTask";
    private const string AbstractMultiMapIndexCreationTaskName = "AbstractMultiMapIndexCreationTask";
    private const string AbstractGenericIndexCreationTaskName = "AbstractGenericIndexCreationTask";
    private const string AbstractJavaScriptIndexCreationTaskName = "AbstractJavaScriptIndexCreationTask";

    /// <summary>Classifies an index type and, for <see cref="RavenIndexKind.Map"/>, its collection.</summary>
    internal static RavenIndexKind Classify(INamedTypeSymbol? indexType, out INamedTypeSymbol? collectionType)
    {
        collectionType = null;
        if (indexType is null) return RavenIndexKind.None;

        var opaque = false;

        for (var current = indexType; current is not null; current = current.BaseType)
        {
            var definition = current.OriginalDefinition;
            if (definition.ContainingNamespace?.ToDisplayString() != RavenIndexesNamespace) continue;

            // Multi-map FIRST: it also derives from AbstractGenericIndexCreationTask<T> and from the
            // non-generic AbstractIndexCreationTask, so any later rule would otherwise claim it and
            // hand back the reduce result as though it were a collection.
            if (definition.Name == AbstractMultiMapIndexCreationTaskName)
                return RavenIndexKind.MultiMap;

            // Arity 1 and arity 2 both name the document FIRST. Accepting only arity 1 is what made a
            // real two-argument map-reduce index resolve to nothing.
            if (definition.Name == AbstractIndexCreationTaskName
                && current.IsGenericType
                && current.TypeArguments.Length is 1 or 2)
            {
                if (current.TypeArguments[0] is INamedTypeSymbol collection)
                {
                    collectionType = collection;
                    return RavenIndexKind.Map;
                }

                // A type parameter rather than a concrete type: still an index, still no collection.
                return RavenIndexKind.Opaque;
            }

            if (definition.Name is AbstractGenericIndexCreationTaskName
                or AbstractIndexCreationTaskName
                or AbstractJavaScriptIndexCreationTaskName)
            {
                // Keep walking — a derived form may still name a collection further down — but
                // remember that this really is an index rather than an unrelated type.
                opaque = true;
            }
        }

        return opaque ? RavenIndexKind.Opaque : RavenIndexKind.None;
    }

    /// <summary>The mapped collection, or <see langword="null"/> when the index names none.</summary>
    internal static INamedTypeSymbol? MappedCollection(INamedTypeSymbol? indexType)
        => Classify(indexType, out var collection) == RavenIndexKind.Map ? collection : null;

    /// <summary>Whether the type derives from the named Spark index base class at any depth.</summary>
    /// <remarks>
    /// Walks rather than testing <c>BaseType</c> directly, because the Spark base class sits
    /// <em>between</em> the user's index and RavenDB's — so every other walk in this file has to keep
    /// working across it, and so does this one.
    /// </remarks>
    internal static bool DerivesFrom(INamedTypeSymbol? indexType, string fullyQualifiedBaseName)
    {
        for (var current = indexType?.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() == fullyQualifiedBaseName)
                return true;
        }

        return false;
    }
}
