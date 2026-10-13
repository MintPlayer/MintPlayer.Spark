using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// One hand-written index claiming to be the default of one or more entity types (<c>docs/default_index_PRD.md</c>,
/// D5), from this compilation or a referenced assembly. A claimed entity's generated index yields: it is emitted
/// without <c>[DefaultIndex]</c>.
/// <para>Strings only (D5): the <c>ForAttributeWithMetadataName</c> transform re-runs on every edit, so this model
/// must compare by value for the steps after it to stay cached (PRD §7, SP2).</para>
/// </summary>
[GenerateEquality]
public partial class DefaultIndexClaimInfo
{
    /// <summary>The index class name without namespace, e.g. <c>People_Overview</c>.</summary>
    public string IndexName { get; set; } = string.Empty;

    /// <summary>Fully-qualified, <c>global::</c>-prefixed index type.</summary>
    public string IndexFullName { get; set; } = string.Empty;

    /// <summary>
    /// Fully-qualified, <c>global::</c>-prefixed entity types the index claims, in the format of
    /// <see cref="GeneratedIndexInfo.EntityFullName"/> so the two can be compared directly.
    /// </summary>
    public List<string> EntityFullNames { get; set; } = new();
}
