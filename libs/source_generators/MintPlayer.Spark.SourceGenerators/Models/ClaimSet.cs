using MintPlayer.ValueComparerGenerator.Attributes;
using System.Collections.Generic;

namespace MintPlayer.Spark.SourceGenerators.Models;

/// <summary>
/// Every <c>[DefaultIndex]</c> claim the generator can see, same-project and referenced, as one value-equal model
/// (<c>docs/default_index_PRD.md</c>, D5).
/// </summary>
[GenerateEquality]
public partial class ClaimSet
{
    public List<DefaultIndexClaimInfo> Claims { get; set; } = new();
}
