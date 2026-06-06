using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.AllFeatures.SourceGenerators.Models;

[AutoValueComparer]
public partial class SparkFullFeatureFlags
{
    public bool HasSpark { get; set; }
    public bool HasSparkUser { get; set; }
    public bool HasAuthorization { get; set; }
    public bool HasMessaging { get; set; }
    public bool HasReplication { get; set; }
}
