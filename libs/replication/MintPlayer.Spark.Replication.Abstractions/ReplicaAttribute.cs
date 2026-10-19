namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Marks a class as a read-only replica of a contract other Spark modules publish (warehouse WX2,
/// replacing <c>[Replicated]</c> without a shim, K3).
/// <para>
/// The replica type derives from the contract record (a <see cref="ReplicationContractAttribute"/>
/// type from a shared contracts package) and adds <c>Id</c>:
/// <code>
/// [Replica] public class Car : Fleet.Contracts.CarV1 { public string? Id { get; set; } }
/// </code>
/// The contract name and version come from the base record. Nothing here names a source module: the
/// sources are configuration, <c>Spark:Replication:Consume:{contract}:Sources</c>, so a generic library
/// never hard-codes one, and several sources may feed one contract.
/// </para>
/// <para>
/// <b>Read-only.</b> The framework refuses New and Delete on a replica type, and a replica is only ever
/// written by its sources' pushes (or their ETL). With <see cref="WriteBack"/>, an edit becomes a
/// SyncAction request to the document's source module, stored in the edit's own commit; the replica
/// changes only when the source pushes its applied state back.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ReplicaAttribute : Attribute
{
    /// <summary>Binds the replica to the contract of its base record, at that record's version.</summary>
    public ReplicaAttribute() { }

    /// <summary>
    /// Binds the replica to <paramref name="contract"/> at <paramref name="versions"/>. Use when the replica
    /// type does not derive from the contract record, or reads several versions of it (the highest version
    /// both sides support is delivered).
    /// </summary>
    public ReplicaAttribute(string contract, params int[] versions)
    {
        Contract = contract;
        Versions = versions;
    }

    /// <summary>The contract name, or <see langword="null"/> to take it from the base record.</summary>
    public string? Contract { get; }

    /// <summary>The contract versions this replica reads, or empty to take the base record's version.</summary>
    public int[] Versions { get; } = [];

    /// <summary>
    /// Whether edits of this replica are sent to the source as write-back requests. Off by default: a
    /// replica is then read-only for everyone, and the SyncAction worker (which holds one of the three
    /// subscriptions Community allows per database) is not registered at all (WX3).
    /// </summary>
    public bool WriteBack { get; init; }
}
