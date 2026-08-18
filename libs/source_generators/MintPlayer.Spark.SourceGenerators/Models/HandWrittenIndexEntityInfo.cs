using MintPlayer.SourceGenerators.Tools;
using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// A hand-written <c>[FromIndex]</c> index entity that needs sort companions contributed to it.
/// <para>
/// The index entity always lives in the application project, so the generator can always add a partial
/// half to it — whether the pair was generated from <c>[GenerateIndex]</c> or written by hand. This is the
/// hand-written case: the developer owns the class, the index and the map; the generator supplies only the
/// companion declarations.
/// </para>
/// <para><strong>The map assignment cannot be generated here.</strong> A generator adds members to a
/// partial class; it cannot reach inside a hand-written <c>Map = ... select new VCar { ... }</c> initializer.
/// So a companion contributed to a hand-written index is declared, stored and sortable but fed by nothing
/// until the developer adds the assignment — which is why the missing-assignment case is a diagnostic rather
/// than a note in a guide.</para>
/// </summary>
[AutoValueComparer]
public partial class HandWrittenIndexEntityInfo
{
    public string Namespace { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    /// <summary>
    /// Namespace and containing-type chain, so a <em>nested</em> index entity is reopened inside its parents
    /// rather than emitted as a top-level class in the namespace — which would not compile.
    /// </summary>
    public PathSpec? PathSpec { get; set; }

    /// <summary>
    /// Whether the class is declared <c>partial</c>. When false nothing can be contributed to it and
    /// <c>SPARK_INDEX_001</c> is reported instead.
    /// </summary>
    public bool IsPartial { get; set; }

    /// <summary>
    /// Companions to contribute — only those not already declared by hand, so a developer who wrote their
    /// own does not get a duplicate member.
    /// </summary>
    public List<IndexPropertyInfo> Companions { get; set; } = new();

    public LocationKey? Location { get; set; }
}
