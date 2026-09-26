using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[GenerateEquality]
public partial class MigrationClassInfo
{
    public string MigrationTypeName { get; set; } = string.Empty;
}
