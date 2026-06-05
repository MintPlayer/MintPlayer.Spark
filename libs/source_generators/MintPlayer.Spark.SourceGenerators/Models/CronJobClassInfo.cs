using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[AutoValueComparer]
public partial class CronJobClassInfo
{
    public string JobTypeName { get; set; } = string.Empty;
}
