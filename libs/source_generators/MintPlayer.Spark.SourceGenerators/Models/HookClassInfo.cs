using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[GenerateEquality]
public partial class HookClassInfo
{
    public string HookTypeName { get; set; } = string.Empty;
}
