namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Marks an assembly as a <b>generic contract library</b> (warehouse PRD §4.3, e.g. <c>MintPlayer.Spark.Fulfilment.Contracts</c>):
/// its <c>[Replicated]</c> classes name no <c>SourceModule</c>, and a host consumes only the ones it binds in
/// <c>Spark:Replication:Sources:{TargetCollection}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both sides of a contract reference the same library: the owner stores the class as its own document (in the
/// collection the class's <c>SourceCollection</c> names), the consumer holds read-only replicas of it. So a host
/// sees every contract of the library, including the ones it owns. Without this marker, a replica class without a
/// source refuses startup ("names no source module"); with it, an unbound class is <b>inert</b> in that host: not a
/// replica, not registered with any owner, and writable as an ordinary document.
/// </para>
/// <para>
/// A replica class that names its <c>SourceModule</c> is always consumed, marker or not.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class SparkReplicaContractsAttribute : Attribute;
