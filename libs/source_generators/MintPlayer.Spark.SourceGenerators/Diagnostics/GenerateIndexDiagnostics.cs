using Microsoft.CodeAnalysis;

namespace MintPlayer.Spark.SourceGenerators.Diagnostics;

/// <summary>
/// Diagnostics reported by <c>GenerateIndexGenerator</c>.
/// <para>
/// Every abort path in that generator reports one of these. That is a deliberate rule, not a courtesy:
/// <c>Producer.Produce</c> discards exceptions, so a producer that fails emits nothing at all, and a
/// missing index degrades at runtime into the RavenDB auto-index that <c>[GenerateIndex]</c> exists to
/// prevent. A silent abort would therefore reintroduce the exact problem the attribute solves, with no
/// signal anywhere.
/// </para>
/// </summary>
internal static class GenerateIndexDiagnostics
{
    private const string Category = "SparkGenerateIndex";

    public static readonly DiagnosticDescriptor ExistingTypeNotPartial = new(
        id: "SPARK_INDEX_001",
        title: "Hand-written index or index-entity class is not partial",
        messageFormat: "'{0}' already exists and is not declared 'partial', so the generated half for entity '{1}' cannot be emitted. Add the 'partial' keyword, or remove [GenerateIndex] and keep the class hand-written.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateIndexName = new(
        id: "SPARK_INDEX_002",
        title: "Two entities generate the same index",
        messageFormat: "Entities '{0}' and '{1}' both generate index '{2}'. Set IndexName on [GenerateIndex] to disambiguate.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoIndexableProperties = new(
        id: "SPARK_INDEX_003",
        title: "Entity has no indexable properties",
        messageFormat: "Entity '{0}' has no properties to index, so no index was generated. Every property is either 'Id', unreadable, an indexer, [IgnoreProperty] or [IgnoreForIndex].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// One index per collection type is a hard ceiling: <c>IIndexRegistry</c> keys registrations by
    /// collection type and silently skips duplicates, so a second index for an entity would be created in
    /// RavenDB and then never used for queries.
    /// </summary>
    public static readonly DiagnosticDescriptor EntityAlreadyHasHandWrittenIndex = new(
        id: "SPARK_INDEX_004",
        title: "Entity already has a hand-written index",
        messageFormat: "Entity '{0}' already has the hand-written index '{1}'. Only one index per entity is registered, so the generated index would be deployed but never queried. Remove [GenerateIndex], or delete the hand-written index.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
