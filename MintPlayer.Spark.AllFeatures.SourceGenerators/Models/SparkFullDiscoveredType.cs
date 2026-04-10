using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.AllFeatures.SourceGenerators.Models;

[AutoValueComparer]
public partial class SparkFullDiscoveredType
{
    /// <summary>
    /// One of: "Context", "User", "Actions", "CustomAction", "Recipient"
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Fully qualified type name (e.g. "global::Fleet.FleetContext").
    /// Empty for existence-only checks (Actions, CustomAction, Recipient).
    /// </summary>
    public string TypeName { get; set; } = string.Empty;
}
