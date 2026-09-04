using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Everything the pump needs to know about one lane, after the builder and configuration have been
/// resolved. One shape serves both delivery modes.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>Concurrent(n)</c> is exactly <c>Ordered</c> with the partition key set to the message's own
/// id.</b> Every message becomes its own partition, so no two share one, so nothing is ordered, so up
/// to <c>n</c> run at once and the exclusion set is a set of message ids — which is precisely "exclude
/// what is in flight". That is not a coincidence: it is the far end of the cardinality axis the whole
/// design argues from (one partition per lane → per build → per message).
/// </para>
/// <para>
/// So there is one pump, not two. The ordered pump is the deeper module — the same interface width,
/// but hiding the ordered window, the head scan, park/wake, exclusion and recovery — and a second
/// implementation would be a shallower module doing a subset of the same work. It would also rot:
/// with one implementation, every concurrent lane in every demo app is extra coverage for the
/// ordered path.
/// </para>
/// </remarks>
internal sealed record LanePlan
{
    public required string LaneName { get; init; }

    /// <summary>
    /// When <see langword="false"/>, the pump substitutes each message's document id for its
    /// partition key. That single substitution is the whole difference between the two modes.
    /// </summary>
    public required bool Ordered { get; init; }

    /// <summary>
    /// Ordered: distinct partitions in flight. Concurrent: messages in flight.
    /// </summary>
    public required int MaxInFlight { get; init; }

    /// <summary>
    /// How many partitions may sit parked on a backoff before the lane reports itself degraded.
    /// </summary>
    /// <remarks>
    /// Not a performance limit. Measured, an exclusion list costs the same at 1 term and at 8192,
    /// even while skipping 41 000 leading rows — so this cap is not what keeps the query cheap. It
    /// earns its place as the only <i>lane-level</i> signal that a dependency is down: per-partition
    /// retry otherwise produces N independent ladders, each locally healthy, with nothing saying
    /// "this lane is broken".
    /// </remarks>
    public int MaxParkedPartitions { get; init; } = 256;

    /// <summary>
    /// Below this, a park is held in memory with a timer; above it the partition is forgotten and the
    /// next drain rediscovers it.
    /// </summary>
    /// <remarks>
    /// The durable write always happens first, so memory is only an accelerator and a restart costs
    /// at most one drain — the delay length has nothing to do with surviving a restart. What the
    /// horizon buys is that a seven-day ladder occupies no memory and no exclusion slot, which keeps
    /// <see cref="MaxParkedPartitions"/> meaning "failing fast right now" rather than "waiting
    /// patiently".
    /// </remarks>
    public TimeSpan ParkHorizon { get; init; } = TimeSpan.FromSeconds(60);

    public required IRetrySchedule Retry { get; init; }

    /// <summary>Selectors by message type, for ordered lanes. Empty for concurrent ones.</summary>
    public IReadOnlyDictionary<Type, Func<object, string>> PartitionSelectors { get; init; }
        = new Dictionary<Type, Func<object, string>>();
}
