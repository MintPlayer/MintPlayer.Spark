using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// What one walk over the referenced assemblies found: <c>[GenerateIndex]</c> entities and
/// <c>[DefaultIndex]</c> claims (D5).
/// <para>
/// A single value-equal model rather than an <c>ImmutableArray</c>: the walk has no syntax to be incremental over
/// and re-runs whenever the compilation changes, so value comparison on its result is what keeps every step
/// after it cached. An array compares by reference and never would.
/// </para>
/// </summary>
[GenerateEquality]
public partial class ReferencedIndexScan
{
    public List<GeneratedIndexInfo> Entities { get; set; } = new();

    public List<DefaultIndexClaimInfo> Claims { get; set; } = new();
}
