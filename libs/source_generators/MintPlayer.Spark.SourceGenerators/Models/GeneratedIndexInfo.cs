using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// Everything the producer needs to emit one index / index-entity pair for a single
/// <c>[GenerateIndex]</c> entity. Deliberately a flat data model of strings and bools: the pipeline
/// compares these for incrementality, so it must hold no Roslyn symbols.
/// </summary>
[AutoValueComparer]
public partial class GeneratedIndexInfo
{
    /// <summary>Fully-qualified entity type, <c>global::</c>-prefixed, for the index's base type.</summary>
    public string EntityFullName { get; set; } = string.Empty;

    /// <summary>Entity type name without namespace, e.g. <c>Car</c>. Used in diagnostics and naming.</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>Generated index class name, e.g. <c>Cars_Overview</c>.</summary>
    public string IndexName { get; set; } = string.Empty;

    /// <summary>
    /// Namespace for both generated types, <c>{RootNamespace}.Indexes</c>.
    /// <para>Deliberately the <em>app's</em> namespace, never the entity's. Indexes and index entities
    /// always belong to the application project — that is what keeps an entity library lean — so a
    /// generated type must not claim a namespace belonging to the assembly that declares the entity.</para>
    /// </summary>
    public string IndexNamespace { get; set; } = string.Empty;

    /// <summary>Generated index-entity (projection) class name, e.g. <c>VCar</c>.</summary>
    public string IndexEntityName { get; set; } = string.Empty;

    /// <summary>Emitted as <c>[Description]</c> on the index class when set.</summary>
    public string? Description { get; set; }

    /// <summary>Lambda parameter for the document collection in the map, e.g. <c>cars</c>.</summary>
    public string CollectionVariable { get; set; } = string.Empty;

    /// <summary>Range variable for a single document in the map, e.g. <c>car</c>.</summary>
    public string ItemVariable { get; set; } = string.Empty;

    public List<IndexPropertyInfo> Properties { get; set; } = new();

    /// <summary>
    /// Properties marked <c>[Search]</c> whose type cannot be searched. Reported as diagnostics rather
    /// than quietly producing an analyzed non-text field with an object-typed sort companion, which is what
    /// the reference implementation does.
    /// </summary>
    public List<InvalidSearchInfo> InvalidSearchProperties { get; set; } = new();

    /// <summary>
    /// Properties marked both <c>[IgnoreProperty]</c> and <c>[Search]</c>. The former keeps them out of the
    /// index, so the latter can never take effect — reported instead of silently dropped.
    /// </summary>
    public List<InvalidSearchInfo> IgnoredSearchProperties { get; set; } = new();

    /// <summary>
    /// Attributes that could not be rendered faithfully onto the index entity. Reported rather than dropped
    /// silently, which is what a whitelist-based carry-over does to anything it does not know.
    /// <para>Reuses <see cref="InvalidSearchInfo"/>: <c>TypeDisplay</c> carries the attribute name.</para>
    /// </summary>
    public List<InvalidSearchInfo> UnrenderableAttributes { get; set; } = new();

    /// <summary>
    /// Complex-typed properties (persist as JSON objects): mapped and stored for projection but
    /// declared <c>FieldIndexing.No</c> — Corax faults per document on a complex field with default
    /// indexing. Reported as SPARK_INDEX_010 so the stored-but-unfilterable column is never a
    /// silent decision.
    /// <para>Reuses <see cref="InvalidSearchInfo"/>: <c>TypeDisplay</c> carries the property's type.</para>
    /// </summary>
    public List<InvalidSearchInfo> ComplexProperties { get; set; } = new();

    /// <summary>Where to anchor diagnostics about this entity.</summary>
    public LocationKey? Location { get; set; }
}

/// <summary>A <c>[Search]</c> on a type that cannot carry it.</summary>
[AutoValueComparer]
public partial class InvalidSearchInfo
{
    public string PropertyName { get; set; } = string.Empty;

    public string TypeDisplay { get; set; } = string.Empty;

    public LocationKey? Location { get; set; }
}

/// <summary>
/// One mapped property: how it is declared on the index entity and how it is fed from the document.
/// </summary>
[AutoValueComparer]
public partial class IndexPropertyInfo
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Fully-qualified type as declared on the index entity, including any <c>?</c>.</summary>
    public string TypeDisplay { get; set; } = string.Empty;

    /// <summary>
    /// Whether the declaration needs <c>= default!</c>. True for a non-nullable reference type, which
    /// would otherwise warn CS8618 in a nullable-enabled compilation.
    /// </summary>
    public bool NeedsDefaultInitializer { get; set; }

    /// <summary>
    /// Expression the map assigns to this field, relative to the document range variable — e.g.
    /// <c>car.Model</c>, or <c>car.Description!.Translations["nl"]</c> for a language field.
    /// </summary>
    public string MapExpression { get; set; } = string.Empty;

    /// <summary>
    /// <c>FieldIndexing</c> value to declare for this field, or <c>null</c> to declare nothing.
    /// <para><strong>Null is the meaningful case for a sort companion.</strong> Leaving a field undeclared
    /// gives it RavenDB's default indexing — a single lower-cased, un-tokenized term — which is what makes
    /// it sortable. Declaring <c>Exact</c> instead was measured as a regression on both ordering
    /// (case-sensitive ordinal) and equality (a case-mismatched <c>==</c> returns nothing).</para>
    /// </summary>
    public string? FieldIndexing { get; set; }

    /// <summary>
    /// Whether this field is a generated sort companion, and therefore carries <c>[IgnoreProperty]</c> and
    /// is excluded from the Spark model.
    /// </summary>
    public bool IsSortCompanion { get; set; }

    /// <summary>
    /// Whether this field is a <c>TranslatedString</c> that must fan out into one field per language.
    /// <para>The expansion happens in the producer, not here: the language set comes from
    /// <c>App_Data/culture.json</c> via a different provider, and the syntax transform that builds this model
    /// cannot see it.</para>
    /// </summary>
    public bool IsTranslated { get; set; }

    /// <summary>
    /// Whether the source property carried <c>[Search]</c>. Retained for translated fields, where the producer
    /// decides per language whether to declare indexing and emit a companion.
    /// </summary>
    public bool IsSearchable { get; set; }

    /// <summary>
    /// Attributes to copy verbatim onto the declaration, already rendered with <c>global::</c> prefixes.
    /// A companion inherits the base property's attributes in addition to its own
    /// <c>[IgnoreProperty]</c>.
    /// </summary>
    public List<string> Attributes { get; set; } = new();
}
