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
    /// The method carrying the generated <c>Index(...)</c> calls goes on the index class, so that class must be
    /// partial. Without it the calls would simply not exist and the fields would be indexed with default
    /// options — searchable text silently not searchable.
    /// </summary>
    public static readonly DiagnosticDescriptor IndexNotPartial = new(
        id: "SPARK_INDEX_009",
        title: "Index class is not partial",
        messageFormat: "Index '{0}' is not declared 'partial', so '{1}()' cannot be generated for it. Add the 'partial' keyword and call '{1}()' from the constructor.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// A complex-typed property persists as a JSON object; Corax refuses to index it with default
    /// options and faults on every document, so the generator maps it with <c>FieldIndexing.No</c> —
    /// stored and projectable (the AsDetail column keeps rendering) but not filterable or sortable.
    /// Warning rather than Info per the house rule: a silently inert column is a decision the author
    /// should see. A [Breadcrumb]-marked property on the complex type upgrades the column to sortable
    /// via a generated companion; [IgnoreForIndex] drops the field from the index entirely.
    /// </summary>
    public static readonly DiagnosticDescriptor ComplexPropertyStoredNotIndexed = new(
        id: "SPARK_INDEX_010",
        title: "Complex property is stored but not indexed",
        messageFormat: "Property '{0}' has complex type '{1}'; it is stored for projection but not indexed, so it cannot be filtered or sorted. Add [Breadcrumb] to a property of '{1}' to make the column sortable, or [IgnoreForIndex] to drop it from the index.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// Two properties of one walked type carry the <c>[Breadcrumb]</c> marker. The ordinal-min name
    /// wins — the same determinism rule as the registry's default index — but the tie is authored
    /// ambiguity worth surfacing.
    /// </summary>
    public static readonly DiagnosticDescriptor MultipleBreadcrumbProperties = new(
        id: "SPARK_INDEX_011",
        title: "Multiple [Breadcrumb] properties on one type",
        messageFormat: "Type '{0}' marks multiple [Breadcrumb] properties; '{1}' (ordinal-min) is used for the sort companion. Remove the extra markers.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>
    /// A <c>[Breadcrumb]</c> marker was found but cannot produce a sort companion — the marked
    /// property is a <c>[Reference]</c> id (a map expression cannot follow a document reference), a
    /// collection (no single value to sort by), a cycle, or the companion name collides with a real
    /// entity property. The complex field falls back to stored-not-indexed.
    /// </summary>
    public static readonly DiagnosticDescriptor BreadcrumbCompanionNotGenerated = new(
        id: "SPARK_INDEX_012",
        title: "[Breadcrumb] cannot produce a sort companion",
        messageFormat: "Property '{0}': {1} The field is stored for projection but stays unsortable.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // SPARK_INDEX_004 ("Entity already has a hand-written index") was removed in #272: it was
    // declared but never reported, and its premise died when IIndexRegistry started retaining
    // every registration per collection type. Multiple indexes over one entity now coexist; the
    // generic query path uses a deterministic default (ordinal-min index name) and the registry
    // warns at registration time. Do not reuse the id.
}
