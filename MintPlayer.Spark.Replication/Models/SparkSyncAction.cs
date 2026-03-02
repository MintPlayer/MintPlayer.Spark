using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Models;

/// <summary>
/// RavenDB document that stores a pending sync action to be processed by the subscription worker.
/// Replaces the message bus approach for sync action delivery.
/// </summary>
public class SparkSyncAction
{
    public string? Id { get; set; }

    /// <summary>The name of the module that owns the entity and should receive the sync action.</summary>
    public required string OwnerModuleName { get; set; }

    /// <summary>The name of the module that initiated the sync action.</summary>
    public required string RequestingModule { get; set; }

    /// <summary>The RavenDB collection name (e.g., "Cars").</summary>
    public required string Collection { get; set; }

    /// <summary>The sync actions to apply on the owner module.</summary>
    public required List<SyncAction> Actions { get; set; }

    /// <summary>Processing status of this sync action document.</summary>
    public ESyncActionStatus Status { get; set; } = ESyncActionStatus.Pending;

    /// <summary>When this document was created.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Last error message if processing failed.</summary>
    public string? LastError { get; set; }
}
