namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Tells the framework which entity types are read-only replicas of another module's contract (warehouse WX2).
/// Implemented by MintPlayer.Spark.Replication; resolved optionally by <see cref="IDatabaseAccess"/>. A replica is
/// written only by its source's pushes, so the unchecked write paths refuse it instead of writing a copy that would
/// silently diverge from the source (edits go through the persistent-object pipeline, which turns them into
/// write-back requests).
/// </summary>
public interface ISparkReplicaTypes
{
    /// <summary>Whether <paramref name="entityType"/> is a <c>[Replica]</c> type.</summary>
    bool IsReplica(Type entityType);
}
