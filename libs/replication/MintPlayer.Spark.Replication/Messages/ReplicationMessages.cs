using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Replication.Messages;

/// <summary>
/// The one queue replication uses (warehouse §4.2): registrations, deploys, pushes, reconciles, queued write-backs and
/// replica-changed notifications. Ordering is not needed (the change-vector guard and op ids decide), so it may run
/// with <c>MaxConcurrency</c> &gt; 1. Replication creates no RavenDB subscription of its own: messaging's one is shared.
/// </summary>
public static class ReplicationQueue
{
    public const string Name = "spark-replication";
}

/// <summary>Consumer: post the registration of every replica read from <see cref="Owner"/> (WD15: only when its set hash changed).</summary>
[MessageQueue(ReplicationQueue.Name)]
public sealed class ReplicaRegistrationMessage
{
    public required string Owner { get; set; }

    /// <summary>When it was queued, for the retry ladder.</summary>
    public DateTime QueuedAtUtc { get; set; }
}

/// <summary>Owner: bring <see cref="Consumer"/>'s delivery in line with its subscriptions (deploy, redeploy or remove).</summary>
[MessageQueue(ReplicationQueue.Name)]
public sealed class ReplicaDeployMessage
{
    public required string Consumer { get; set; }
}

/// <summary>
/// Owner, HTTP transport: push the current state of <see cref="Ids"/> to one consumer. The recipient loads each
/// document when it runs, so a burst of writes becomes one push of the latest state; an id that no longer exists is
/// pushed as a tombstone.
/// </summary>
[MessageQueue(ReplicationQueue.Name)]
public sealed class ReplicaPushMessage
{
    public required string Consumer { get; set; }

    public required string TargetCollection { get; set; }

    public List<string> Ids { get; set; } = [];

    /// <summary>When the push stops retrying an unreachable consumer (the reconcile repairs it later).</summary>
    public DateTime GiveUpAtUtc { get; set; }

    /// <summary>Attempts made, for the retry ladder.</summary>
    public int Attempts { get; set; }
}

/// <summary>Owner, HTTP transport: reconcile one subscription now (the initial load after an approval).</summary>
[MessageQueue(ReplicationQueue.Name)]
public sealed class ReplicaReconcileMessage
{
    public required string Consumer { get; set; }

    public required string TargetCollection { get; set; }
}

/// <summary>Consumer: send a queued write-back to its owner.</summary>
[MessageQueue(ReplicationQueue.Name)]
public sealed class SyncActionDispatchMessage
{
    public required string SyncActionId { get; set; }
}

/// <summary>
/// Consumer: a push changed a replica; tells every <c>IReplicaChanged&lt;T&gt;</c>. Enqueued in the session that applied
/// the push, so it is durable.
/// </summary>
[MessageQueue(ReplicationQueue.Name)]
public sealed class ReplicaChangedMessage
{
    /// <summary>The replica collection (resolves the replica type through the catalog).</summary>
    public required string TargetCollection { get; set; }

    public required string Id { get; set; }

    public required string SourceModule { get; set; }

    /// <summary>The replica before, as JSON text; null when it was created.</summary>
    public string? PreviousJson { get; set; }

    /// <summary>The replica now, as JSON text; null when the source deleted it.</summary>
    public string? CurrentJson { get; set; }

    public string? SourceChangeVector { get; set; }
}
