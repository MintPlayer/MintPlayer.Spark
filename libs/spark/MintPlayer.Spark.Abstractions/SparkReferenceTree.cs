namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Declares a Reference attribute's candidates to be a tree (warehouse PRD WX9): the picker lists the
/// roots first and loads a node's children when it is expanded, instead of one flat list.
/// </summary>
/// <remarks>
/// Hand-set in the model JSON on the referencing attribute and preserved across synchronize (like
/// <see cref="EntityAttributeDefinition.ReferenceDisplayType"/>):
/// <code>
/// "referenceTree": { "parentAttribute": "Parent", "childCountAttribute": "ChildCount" }
/// </code>
/// <b>How the levels are read.</b> The picker runs the attribute's own reference query with a column
/// filter on <see cref="ParentAttribute"/>: <c>includes: [null]</c> for the roots, <c>includes: [id]</c>
/// for the children of <c>id</c>. That is the existing column filter (#431), so the rows come back under
/// the same row security, search and paging as the flat picker; nothing new is exposed. Search stays
/// flat: it matches across every level.
/// <para>
/// ⚠️ <see cref="ParentAttribute"/> must be filterable on the reference query's surface (shown on the
/// query, <c>canFilter</c> not <see langword="false"/>). A refused filter does not fail, it leaves the
/// rows unnarrowed, so every node would list the whole set as its children. <c>--spark-verify-model</c>
/// checks the declaration against the target type for this reason.
/// </para>
/// </remarks>
public sealed class SparkReferenceTree
{
    /// <summary>
    /// The attribute of the <b>referenced</b> type that holds a row's parent id: a Reference to that same
    /// type, empty on a root.
    /// </summary>
    public required string ParentAttribute { get; set; }

    /// <summary>
    /// Optional: an attribute of the referenced type holding the number of children (or a boolean "has
    /// children"). A row whose value is <c>0</c> or <see langword="false"/> is drawn as a leaf, without an
    /// expand chevron. Absent: every node is expandable, and a node without children expands to nothing.
    /// </summary>
    public string? ChildCountAttribute { get; set; }
}
