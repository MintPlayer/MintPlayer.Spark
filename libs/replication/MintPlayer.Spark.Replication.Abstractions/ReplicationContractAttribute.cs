namespace MintPlayer.Spark.Replication.Abstractions;

/// <summary>
/// Names the replication contract a record type is (warehouse WX2): its permanent lower-kebab name and
/// its version. The record lives in a contracts package both sides reference, so the owner projects
/// into it (<c>SparkReplicationOptions.Publish</c>)
/// and the consumer's replica type reuses it by deriving from it (<see cref="ReplicaAttribute"/>).
/// <para>
/// A contract record has public settable properties and no <c>Id</c>: the document id travels beside
/// the payload, and the replica type adds it.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ReplicationContractAttribute : Attribute
{
    /// <param name="name">The permanent lower-kebab name, e.g. <c>cars</c> or <c>fulfilment-orders</c>.</param>
    /// <param name="version">The version, 1 or higher. Versions of one contract run side by side.</param>
    public ReplicationContractAttribute(string name, int version)
    {
        Name = name;
        Version = version;
    }

    /// <summary>The contract's permanent lower-kebab name.</summary>
    public string Name { get; }

    /// <summary>The contract version.</summary>
    public int Version { get; }

    /// <summary>The <see cref="ReplicationContractAttribute"/> on <paramref name="type"/> itself, or <see langword="null"/>.</summary>
    public static ReplicationContractAttribute? Of(Type type)
        => (ReplicationContractAttribute?)GetCustomAttribute(type, typeof(ReplicationContractAttribute), inherit: false);
}
