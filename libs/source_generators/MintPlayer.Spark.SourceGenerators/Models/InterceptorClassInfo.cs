using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[GenerateEquality]
public partial class InterceptorClassInfo
{
    public string InterceptorTypeName { get; set; } = string.Empty;
}
