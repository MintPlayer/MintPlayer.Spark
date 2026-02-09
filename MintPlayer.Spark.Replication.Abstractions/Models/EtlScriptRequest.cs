namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// Payload sent to a source module's /spark/etl/deploy endpoint,
/// requesting it to create RavenDB ETL tasks that push data to the requesting module.
/// </summary>
public class EtlScriptRequest
{
    /// <summary>Name of the requesting module (the one that wants the data).</summary>
    public required string RequestingModule { get; set; }

    /// <summary>Database name of the requesting module (ETL target).</summary>
    public required string TargetDatabase { get; set; }

    /// <summary>RavenDB URLs of the requesting module (for connection string).</summary>
    public required string[] TargetUrls { get; set; }

    /// <summary>Individual ETL transformation scripts, one per source collection.</summary>
    public required List<EtlScriptItem> Scripts { get; set; }
}
