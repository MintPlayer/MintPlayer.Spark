namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// Payload sent to an owner module's /spark/sync/apply endpoint,
/// requesting it to apply CRUD operations on entities it owns.
/// </summary>
public class SyncActionRequest
{
    /// <summary>Name of the module sending the sync actions.</summary>
    public required string RequestingModule { get; set; }

    /// <summary>The sync actions to apply.</summary>
    public required List<SyncAction> Actions { get; set; }
}
