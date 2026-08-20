namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Requests a generated RavenDB index and matching projection class for this entity, replacing the
/// hand-written pair described in <c>docs/guide-queries-and-sorting.md</c>.
/// <para>
/// For an entity <c>Car</c> the generator emits an index <c>Cars_Overview :
/// AbstractIndexCreationTask&lt;Car&gt;</c> and a projection
/// <c>[FromIndex(typeof(Cars_Overview))] partial class VCar</c>, both <c>partial</c> so they can be
/// extended by hand. The index maps every model property, calls
/// <c>StoreAllFields(FieldStorage.Yes)</c> so <c>ProjectInto&lt;VCar&gt;()</c> can read computed
/// fields, and ends with a call to <c>partial void OnInitialize()</c> — the sanctioned place to add
/// index configuration without giving up generation.
/// </para>
/// <para>
/// Properties marked <see cref="SearchAttribute"/> additionally get a companion sort field; see that
/// attribute for why sorting requires one.
/// </para>
/// <para><strong>The generated types land in the project that references the generator, not in the
/// project that declares the entity.</strong> Entities commonly live in a class library while indexes
/// belong to the app, so the generator reads <c>[GenerateIndex]</c> from referenced assemblies too.
/// Reference the generator from the app and the index is discovered and deployed automatically by
/// <c>UseSpark()</c>; reference it from the library instead and the index lands in the library, where
/// it is invisible unless the app also calls <c>spark.AddIndexesFrom(...)</c>.</para>
/// <para>
/// An entity may back any number of indexes; each Spark query declares the one it runs against via its
/// <c>indexName</c>. The generated index carries <see cref="DefaultIndexAttribute"/> — electing its
/// projection to shape the entity's model file — unless <see cref="IsDefault"/> is set to
/// <c>false</c>, e.g. because a hand-written index alongside it is the intended default.
/// </para>
/// </summary>
/// <example>
/// <code>
/// [GenerateIndex]
/// public partial class Car
/// {
///     public string? Id { get; set; }
///     [Search] public string Model { get; set; } = string.Empty;
///     public int Year { get; set; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class GenerateIndexAttribute : Attribute
{
    /// <summary>
    /// Overrides the generated index class name. Defaults to <c>{PluralEntityName}_Overview</c>.
    /// <para><strong>Renaming an index re-indexes the database.</strong> RavenDB identifies an index by
    /// its class name, so changing this on a deployed app discards the old index and rebuilds from
    /// scratch.</para>
    /// </summary>
    public string? IndexName { get; set; }

    /// <summary>
    /// Overrides the generated index-entity (projection) class name. Defaults to <c>V{EntityName}</c>.
    /// </summary>
    public string? IndexEntityName { get; set; }

    /// <summary>
    /// Emitted as <c>[Description]</c> on the generated index class. Purely documentary.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether the generated index carries <see cref="DefaultIndexAttribute"/>, electing its projection
    /// to shape the entity's model file. Defaults to <c>true</c>. Set <c>false</c> when another index
    /// over the same entity is the intended default and carries the marker itself.
    /// </summary>
    public bool IsDefault { get; set; } = true;
}
