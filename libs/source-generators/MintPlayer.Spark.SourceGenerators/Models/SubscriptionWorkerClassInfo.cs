using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[AutoValueComparer]
public partial class SubscriptionWorkerClassInfo
{
    public string WorkerTypeName { get; set; } = string.Empty;
}
