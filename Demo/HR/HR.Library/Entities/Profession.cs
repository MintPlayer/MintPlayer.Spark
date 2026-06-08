using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

[Breadcrumb("{Description}")]
public class Profession
{
    public string? Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Regime { get; set; } = string.Empty;
}
