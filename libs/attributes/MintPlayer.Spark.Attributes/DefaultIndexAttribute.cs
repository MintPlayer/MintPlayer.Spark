namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Claims an index as the <b>default index</b> of one or more entity types
/// (<c>docs/default_index_PRD.md</c>, D1–D4). The default index of <c>T</c> shapes <c>T</c>'s model file
/// (through its <c>[FromIndex]</c> view, when it has one) and serves <c>T</c>'s lookup: minted
/// <c>Database.*</c> queries over <c>T</c> are stamped with its name, and a <c>[Reference(typeof(T))]</c>
/// that names no query resolves through them. A reference that names a query overrides the lookup.
/// <list type="bullet">
/// <item><b>Single-map</b> (<c>AbstractIndexCreationTask&lt;T&gt;</c> and its Spark/JS counterparts): the
/// types are optional and default to the mapped <c>T</c>. When given, they must be exactly <c>T</c>.</item>
/// <item><b>Multi-map</b> (<c>SparkMultiMapIndexCreationTask&lt;TReduce&gt;</c>): the types are required —
/// a referenced assembly's metadata holds no constructor bodies, so the <c>AddMap&lt;X&gt;</c> calls cannot
/// be read from there. A multi-map default must derive from <c>SparkMultiMapIndexCreationTask</c>: Spark
/// injects a collection discriminator into each map and filters on it, so <c>T</c>'s queries never return
/// another mapped collection's rows as <c>T</c> (which would also bypass that collection's row policy).</item>
/// </list>
/// <para>
/// Election, per entity type: a type claimed by exactly one index has that default, with or without a
/// view (a viewless default shapes nothing and its queries return entity documents). An unclaimed type whose
/// mapping indexes include exactly one with a view has that one implicitly. Two claims for a type, or several
/// viewed indexes and no claim, fail synchronize and startup with an error naming the candidates — the
/// framework never guesses.
/// </para>
/// <para>
/// <c>[GenerateIndex]</c> marks its generated index as the default unless a hand-written index claims the
/// entity already.
/// </para>
/// </summary>
/// <example>
/// <code>
/// [DefaultIndex]                       // single-map: claims Car
/// public class Cars_Overview : SparkIndexCreationTask&lt;Car&gt; { … }
///
/// [DefaultIndex(typeof(Car), typeof(Bike))]   // multi-map: lists what it claims
/// public class Vehicles : SparkMultiMapIndexCreationTask&lt;VehicleEntry&gt; { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class DefaultIndexAttribute : Attribute
{
    /// <param name="entityTypes">
    /// The entity types this index is the default of. Empty means "the mapped type" and is valid on a
    /// single-map index only.
    /// </param>
    public DefaultIndexAttribute(params Type[] entityTypes)
    {
        EntityTypes = entityTypes ?? [];
    }

    /// <summary>
    /// The declared entity types; empty when the marker is bare (a single-map's mapped type is then implied).
    /// </summary>
    /// <remarks>
    /// ⚠️ A bare <c>[DefaultIndex]</c> reaches a source generator as <b>one</b> constructor argument holding an
    /// empty array, not as zero arguments (PRD §7, SP2): test the array's length.
    /// </remarks>
    public IReadOnlyList<Type> EntityTypes { get; }
}
