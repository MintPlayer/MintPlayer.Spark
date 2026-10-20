namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Marks a <c>[Replicated]</c> class whose replication constants were generated. Never implement it by
/// hand: <c>MintPlayer.Spark.LibraryGenerators</c> adds it together with the constants
/// (<c>ReplicationSourceCollection</c>, <c>ReplicationTargetCollection</c>, <c>ReplicationSourceModule</c>,
/// <c>ReplicationWriteBack</c>, <c>ReplicationManifest</c>, <c>ReplicationScript</c>, <c>ReplicationHash</c>),
/// which the runtime reads from the types listed in <see cref="SparkReplicaTypesAttribute"/>.
/// </summary>
public interface IReplicatedEntity
{
}
