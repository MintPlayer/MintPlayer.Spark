using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// Message sent via the durable message bus to forward a sync action to the owner module.
/// Queue name is overridden per-collection at dispatch time (e.g., "spark-sync-Cars").
/// </summary>
[MessageQueue("spark-sync")]
public class SyncActionDeploymentMessage
{
    /// <summary>The name of the module that owns the entity.</summary>
    public required string OwnerModuleName { get; set; }

    /// <summary>The sync action request payload to POST to the owner module.</summary>
    public required SyncActionRequest Request { get; set; }
}
