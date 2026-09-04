namespace MintPlayer.Spark.Messaging.Models;

public class SparkMessage
{
    public string? Id { get; set; }
    public string QueueName { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Broadcast order within a partition. Strictly increasing, issued by
    /// <see cref="Services.MessageSequence"/>.
    /// </summary>
    /// <remarks>
    /// This — not <see cref="CreatedAtUtc"/> and emphatically not <see cref="Id"/> — is the ordering
    /// key. <c>ThenBy(m =&gt; m.Id)</c> compiles to a lexicographic <c>order by id()</c> over
    /// non-zero-padded hilo ids, so <c>SparkMessages/10-A</c> sorts before <c>SparkMessages/2-A</c>.
    /// See <see cref="Services.MessageSequence"/> for the measurement.
    /// </remarks>
    public long Sequence { get; set; }

    /// <summary>
    /// The ordering domain this message belongs to — the build, pull request, repository or document
    /// its messages are ordered <i>within</i>. Empty on unordered lanes.
    /// </summary>
    /// <remarks>
    /// Resolved once, producer-side, by the lane's selector, and persisted. Never recomputed: a
    /// selector that changed would move existing messages between partitions and split a partition
    /// in half invisibly.
    /// </remarks>
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>
    /// When a retry becomes due. A head with a future value <b>blocks its partition</b> — that is
    /// what makes ordering hold across the retry path.
    /// </summary>
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// When a <i>delayed broadcast</i> becomes eligible. Unlike <see cref="NextAttemptAtUtc"/> this
    /// does <b>not</b> block its partition.
    /// </summary>
    /// <remarks>
    /// The two are separate fields because they demand opposite treatment. A backing-off head must
    /// hold its partition, or a newer message overtakes it. A delayed message must not: a delay is a
    /// scheduling instruction, not a dependency, and blocking on one would mean
    /// <c>DelayBroadcastAsync(m, 5m)</c> silently freezing everything in <c>m</c>'s partition for
    /// five minutes — which no caller could intend. Sharing one field also made a delayed message
    /// look like it was already on a retry rung.
    /// </remarks>
    public DateTime? VisibleAtUtc { get; set; }

    /// <summary>
    /// Number of times this message has been picked up for processing (informational).
    /// Per-handler attempt counts are tracked in <see cref="Handlers"/>.
    /// </summary>
    public int AttemptCount { get; set; }
    public EMessageStatus Status { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Per-handler execution state. Populated when the message is first picked up for processing.
    /// </summary>
    public List<HandlerExecution> Handlers { get; set; } = new();

}
