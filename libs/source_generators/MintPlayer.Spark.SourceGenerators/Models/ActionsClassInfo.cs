using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[AutoValueComparer]
public partial class ActionsClassInfo
{
    public string ActionsTypeName { get; set; } = string.Empty;
    public string EntityTypeName { get; set; } = string.Empty;
}
