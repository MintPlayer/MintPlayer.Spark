namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// A single ETL transformation script for a specific source collection.
/// </summary>
public class EtlScriptItem
{
    /// <summary>Source collection name in the owning module's database.</summary>
    public required string SourceCollection { get; set; }

    /// <summary>JavaScript ETL transformation script (RavenDB ETL syntax).</summary>
    public required string Script { get; set; }
}
