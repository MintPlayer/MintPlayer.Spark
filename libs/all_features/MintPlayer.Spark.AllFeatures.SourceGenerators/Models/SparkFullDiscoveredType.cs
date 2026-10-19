using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.AllFeatures.SourceGenerators.Models;

[GenerateEquality]
public partial class SparkFullDiscoveredType
{
    /// <summary>
    /// One of: "Context", "User". Every other registration is SparkDiscoveryGenerator's
    /// (MintPlayer.Spark.SourceGenerators), reached through the generated <c>AddSparkDiscovered</c>.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Fully qualified type name (e.g. "global::Fleet.FleetContext").
    /// </summary>
    public string TypeName { get; set; } = string.Empty;
}
