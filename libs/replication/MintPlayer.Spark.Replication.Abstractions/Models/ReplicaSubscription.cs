using System.Text.Json.Serialization;
using MintPlayer.Spark.Replication.Abstractions.Configuration;

namespace MintPlayer.Spark.Replication.Abstractions.Models;

/// <summary>
/// What a consumer sends a source at startup, over its module certificate, to <c>POST /spark/replicas/subscribe</c>
/// (warehouse WX2, replacing the ETL deployment broadcast). It names contracts only.
/// <para>
/// No script (F16): the owner generates every ETL transform from its own contracts. No database, URL or
/// address (F19): every destination is the authenticated module's <c>SparkModules</c> entry.
/// </para>
/// </summary>
public class ReplicaSubscriptionRequest
{
    /// <summary>The consumer. Must match the presented module certificate.</summary>
    public required string RequestingModule { get; set; }

    /// <summary>
    /// Every contract the consumer reads from this source — the complete set. A contract the owner held a
    /// subscription for and that is missing here is withdrawn, together with its ETL transform (F18).
    /// An empty list withdraws everything.
    /// </summary>
    public required List<ReplicaSubscriptionItem> Contracts { get; set; }
}

/// <summary>One contract a consumer reads.</summary>
public class ReplicaSubscriptionItem
{
    /// <summary>The contract's lower-kebab name.</summary>
    public required string Contract { get; set; }

    /// <summary>The versions the consumer's replica type reads. The owner delivers the highest one it publishes.</summary>
    public required int[] Versions { get; set; }

    /// <summary>
    /// The consumer's collection for the replica type: the ETL load target. Validated by the owner as a
    /// plain identifier before it goes anywhere near a script.
    /// </summary>
    public required string TargetCollection { get; set; }
}

/// <summary>The owner's answer to a <see cref="ReplicaSubscriptionRequest"/>.</summary>
public class ReplicaSubscriptionResult
{
    /// <summary>The transport the owner delivers with. On <see cref="ReplicationTransport.Http"/> the consumer reconciles.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ReplicationTransport>))]
    public ReplicationTransport Transport { get; set; }

    /// <summary>The subscriptions the owner now holds for the consumer, with the version it delivers.</summary>
    public List<ReplicaSubscriptionGranted> Contracts { get; set; } = [];

    /// <summary>Contracts the owner withdrew in this call.</summary>
    public List<string> Withdrawn { get; set; } = [];

    /// <summary>Why the request was refused, when it was.</summary>
    public string? Error { get; set; }
}

/// <summary>A subscription the owner holds.</summary>
public class ReplicaSubscriptionGranted
{
    public required string Contract { get; set; }

    /// <summary>The version delivered: the highest one both sides support.</summary>
    public required int Version { get; set; }
}
