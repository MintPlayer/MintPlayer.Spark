namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// Response from the /spark/etl/deploy endpoint after processing an ETL deployment request.
/// </summary>
public class EtlDeploymentResult
{
    /// <summary>
    /// <see cref="Error"/> when the owner's RavenDB licence has no ETL (Community). The deployment is
    /// answered 409 and the consumer's message is dead-lettered once with this reason instead of
    /// being retried (F18).
    /// </summary>
    public const string NotLicensedError = "ETL_NOT_LICENSED";

    public bool Success { get; set; }
    public int TasksCreated { get; set; }
    public int TasksUpdated { get; set; }
    public int TasksRemoved { get; set; }
    public string? Error { get; set; }
}
