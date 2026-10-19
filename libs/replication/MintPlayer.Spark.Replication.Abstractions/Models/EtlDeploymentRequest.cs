namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// Payload a consumer sends to an owning module's <c>/spark/etl/deploy</c> endpoint: the contracts
/// it reads, and nothing the owner would execute or connect to.
/// <para>
/// It carries no script (F16): the owner generates every transform from its own contract. It
/// carries no target database or URLs either (F19): the owner takes the destination from the
/// authenticated module's <c>SparkModules</c> registry entry.
/// </para>
/// </summary>
public class EtlDeploymentRequest
{
    /// <summary>Name of the requesting module (the one that wants the data).</summary>
    public required string RequestingModule { get; set; }

    /// <summary>The contracts the requesting module reads from this owner.</summary>
    public required List<EtlContractRequest> Contracts { get; set; }
}

/// <summary>One contract a consumer reads, and the collection its replicas live in.</summary>
public class EtlContractRequest
{
    /// <summary>The contract's lower-kebab name.</summary>
    public required string Contract { get; set; }

    /// <summary>The contract version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// The consumer's collection for the replica type. The owner validates it as a plain identifier
    /// before it names the ETL load target.
    /// </summary>
    public required string TargetCollection { get; set; }
}
