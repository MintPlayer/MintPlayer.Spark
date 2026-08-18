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

    /// <summary>Where to anchor diagnostics about this entity.</summary>
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
}
