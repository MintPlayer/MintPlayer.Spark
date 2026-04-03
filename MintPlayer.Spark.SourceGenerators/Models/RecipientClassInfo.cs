using MintPlayer.ValueComparerGenerator.Attributes;

namespace MintPlayer.Spark.SourceGenerators.Models;

[AutoValueComparer]
public partial class RecipientClassInfo
{
    public string RecipientTypeName { get; set; } = string.Empty;
    public string MessageTypeName { get; set; } = string.Empty;
    public bool IsCheckpointRecipient { get; set; }
}
