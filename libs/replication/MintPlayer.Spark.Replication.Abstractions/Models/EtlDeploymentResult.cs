namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// Response from the /spark/etl/deploy endpoint after processing an ETL script request.
/// </summary>
public class EtlDeploymentResult
{
    public bool Success { get; set; }
    public int TasksCreated { get; set; }
    public int TasksUpdated { get; set; }
    public int TasksRemoved { get; set; }
    public string? Error { get; set; }
}
