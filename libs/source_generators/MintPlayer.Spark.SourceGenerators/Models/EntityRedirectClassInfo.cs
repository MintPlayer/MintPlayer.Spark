using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[GenerateEquality]
public partial class EntityRedirectClassInfo
{
    public string RedirectTypeName { get; set; } = string.Empty;
}
