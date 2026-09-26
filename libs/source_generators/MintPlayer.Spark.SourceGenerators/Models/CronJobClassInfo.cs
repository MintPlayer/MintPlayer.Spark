using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[GenerateEquality]
public partial class CronJobClassInfo
{
    public string JobTypeName { get; set; } = string.Empty;
}
