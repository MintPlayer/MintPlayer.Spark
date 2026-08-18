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
        messageFormat: "Index entity '{0}' has [Search] properties needing sort companions but is not declared 'partial', so nothing can be contributed to it. Add the 'partial' keyword.",
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
    /// The reference implementation applies <c>FieldIndexing.Search</c> to an object-typed field without
    /// complaint, and gives it an object-typed sort companion. Both are meaningless. Diagnose instead.
    /// </summary>
    public static readonly DiagnosticDescriptor SearchOnUnsupportedType = new(
        id: "SPARK_INDEX_005",
        title: "[Search] on a type that cannot be searched",
        messageFormat: "Property '{0}' is marked [Search] but its type '{1}' is not searchable. [Search] applies to string, a collection of strings, or TranslatedString.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// <c>[IgnoreProperty]</c> keeps a property out of the generated index as well as out of the model, so a
    /// <c>[Search]</c> beside it can never take effect. Without this the combination reads as "indexed but
    /// hidden" — which is what the reference implementation does — and does nothing at all here.
    /// </summary>
    public static readonly DiagnosticDescriptor SearchOnIgnoredProperty = new(
        id: "SPARK_INDEX_006",
        title: "[Search] has no effect on an [IgnoreProperty] property",
        messageFormat: "Property '{0}' is marked both [IgnoreProperty] and [Search]. [IgnoreProperty] excludes it from the generated index, so [Search] does nothing. Remove one of the two.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// Attribute carry-over is a deny-list, so anything not a generator directive is copied. When an
    /// attribute's arguments cannot be rendered as source it is skipped -- and said out loud, because the
    /// alternative is the reference implementation's behaviour of silently dropping whatever its whitelist
    /// does not cover.
    /// </summary>
    public static readonly DiagnosticDescriptor AttributeNotCopied = new(
        id: "SPARK_INDEX_007",
        title: "Attribute could not be copied to the index entity",
        messageFormat: "Attribute '{0}' on property '{1}' could not be rendered onto the generated index entity and was skipped. Declare it by hand on a partial half of the index entity if it is needed there.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// A non-partial SparkContext cannot be extended, so generated query roots would simply not appear. The
    /// absence of a member is exactly the kind of silence this generator is built to avoid.
    /// </summary>
    public static readonly DiagnosticDescriptor ContextNotPartial = new(
        id: "SPARK_INDEX_008",
        title: "SparkContext is not partial",
        messageFormat: "Context '{0}' is not declared 'partial', so index-backed query roots cannot be added to it. Add the 'partial' keyword.",
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
