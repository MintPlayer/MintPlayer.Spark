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
    /// Fully-qualified, <c>global::</c>-prefixed name of the index entity.
    /// <para>The generated method lives on the INDEX class, which may sit in a different namespace — so an
    /// unqualified <c>nameof(VCar.X)</c> there does not resolve. Co-locating them is the convention, but the
    /// generator cannot rely on a consumer following it.</para>
    /// </summary>
    public string FullName { get; set; } = string.Empty;

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

    /// <summary>
    /// Fields needing an <c>Index(...)</c> call, emitted as a method the hand-written constructor calls.
    /// <para>Declaring searchability on the index entity via <c>[Search]</c> and then repeating it as an
    /// <c>Index(nameof(VCar.X), FieldIndexing.Search)</c> line in the constructor says the same thing twice, and
    /// the two drift. The attribute is the single declaration; the calls are generated from it.</para>
    /// </summary>
    public List<IndexPropertyInfo> IndexedFields { get; set; } = new();

    /// <summary>The index class named by <c>[FromIndex]</c>, which receives the generated method.</summary>
    public string IndexClassName { get; set; } = string.Empty;

    public PathSpec? IndexPathSpec { get; set; }

    /// <summary>
    /// Whether the index class is <c>partial</c>. Without it the method cannot be contributed and
    /// <c>SPARK_INDEX_009</c> is reported rather than the calls quietly not existing.
    /// </summary>
    public bool IsIndexPartial { get; set; }

    public LocationKey? IndexLocation { get; set; }

    public LocationKey? Location { get; set; }
}
