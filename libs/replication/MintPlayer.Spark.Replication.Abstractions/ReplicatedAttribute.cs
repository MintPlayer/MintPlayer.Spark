namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Marks a class as a read-only replica of a contract another Spark module publishes. The class
/// reuses the contract record's shape: the owner projects its documents into that record
/// (<see cref="Configuration.SparkReplicationOptions.Publish{TSource, TContract}"/>), and this
/// module stores the copies in the replica type's own collection.
/// <para>
/// On startup the framework collects every <c>[Replicated]</c> type, groups them by
/// <see cref="SourceModule"/>, and asks each owner for <c>(contract, version, collection)</c>. The
/// owner generates the ETL transform from its own contract: a consumer never sends JavaScript (F16).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ReplicatedAttribute : Attribute
{
    /// <summary>
    /// The name of the module that owns the original data.
    /// Must match the ModuleName in the source module's SparkReplicationOptions.
    /// </summary>
    public required string SourceModule { get; init; }

    /// <summary>
    /// The lower-kebab name of the contract the owner publishes, e.g. <c>cars</c>.
    /// </summary>
    public required string Contract { get; init; }

    /// <summary>The contract version this replica reads. Defaults to 1.</summary>
    public int Version { get; init; } = 1;

    /// <summary>
    /// The RavenDB collection in the source database that write-back (SyncAction) targets.
    /// If null, inferred from <see cref="OriginalType"/> or the decorated class name. Not used by
    /// the read path: the owner's contract names its source.
    /// </summary>
    public string? SourceCollection { get; init; }

    /// <summary>
    /// The original CLR type in the source module, used to infer the write-back collection when
    /// <see cref="SourceCollection"/> is not set.
    /// </summary>
    public Type? OriginalType { get; init; }
}
