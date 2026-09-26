using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[GenerateEquality]
public partial class SubscriptionWorkerClassInfo
{
    public string WorkerTypeName { get; set; } = string.Empty;
}
