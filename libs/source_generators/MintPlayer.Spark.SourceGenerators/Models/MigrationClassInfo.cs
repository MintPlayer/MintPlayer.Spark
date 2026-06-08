using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[AutoValueComparer]
public partial class MigrationClassInfo
{
    public string MigrationTypeName { get; set; } = string.Empty;
}
