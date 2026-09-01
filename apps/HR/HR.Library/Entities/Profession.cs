using MintPlayer.Spark.Abstractions;

namespace HR.Entities;

// Breadcrumb template in App_Data/Model/Profession.json: "{Description}".
public class Profession
{
    public string? Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Regime { get; set; } = string.Empty;
}
