using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[GenerateEquality]
public partial class ActionsClassInfo
{
    public string ActionsTypeName { get; set; } = string.Empty;
    public string EntityTypeName { get; set; } = string.Empty;

    /// <summary>
    /// True when a base class has the same simple name and is itself a non-abstract actions class (#660 G17): the
    /// resolver would find both by name, so the generator records this one as the override.
    /// </summary>
    public bool OverridesBaseActions { get; set; }
}
