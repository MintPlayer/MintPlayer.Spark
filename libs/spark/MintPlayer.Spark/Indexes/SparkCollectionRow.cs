namespace MintPlayer.Spark;

/// <summary>
/// The collection discriminator of a multi-map default index (<c>docs/default_index_PRD.md</c>, D4):
/// the field Spark injects into each map of a <see cref="SparkMultiMapIndexCreationTask{TReduceResult}"/>
/// that carries <c>[DefaultIndex(typeof(X), …)]</c>, holding the RavenDB collection the entry came from.
/// </summary>
/// <remarks>
/// <para>
/// A multi-map returns the entries of every collection it maps. Queried as <c>Query&lt;Car, Vehicles&gt;()</c>
/// with no filter, a <c>Bike</c> comes back materialized as a <c>Car</c> — the wrong row, rendered with the
/// wrong type's template, and outside <c>Bike</c>'s row policy. The query executor therefore narrows a
/// multi-map default to the queried type's collection, through this shape:
/// <code>
/// session.Query&lt;SparkCollectionRow, Vehicles&gt;()
///        .Where(r =&gt; r.SparkCollection == "Cars")
///        .OfType&lt;Car&gt;()
/// </code>
/// before row security, filters, search, sort and the view projection compose on top (SP1).
/// </para>
/// <para>
/// Public so a hand-written query against a multi-map default can apply the same narrowing.
/// </para>
/// </remarks>
public sealed class SparkCollectionRow
{
    /// <summary>The index field's name, <c>SparkCollection</c>.</summary>
    public const string FieldName = nameof(SparkCollection);

    /// <summary>The RavenDB collection of the document this index entry was mapped from.</summary>
    public string? SparkCollection { get; set; }
}
