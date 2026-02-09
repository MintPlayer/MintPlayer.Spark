namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// Stored in the shared SparkModules database so that modules can discover each other.
/// Document ID: "moduleInformations/{AppName}"
/// </summary>
public class ModuleInformation
{
    public string? Id { get; set; }
    public required string AppName { get; set; }
    public required string AppUrl { get; set; }
    public required string DatabaseName { get; set; }
    public string[] DatabaseUrls { get; set; } = [];
    public DateTime RegisteredAtUtc { get; set; }
}
