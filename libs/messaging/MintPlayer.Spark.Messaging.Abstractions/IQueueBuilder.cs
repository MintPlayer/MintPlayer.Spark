using System.Linq.Expressions;

namespace MintPlayer.Spark.Messaging.Abstractions;

/// <summary>
/// Declares how one lane behaves. A lane is an <b>isolation</b> unit: lanes never block one another.
/// </summary>
/// <remarks>
/// Delivery mode is chosen by calling a method that returns a mode-specific builder, so the
/// contradiction "strictly ordered, four at a time" has no method to call rather than being rejected
/// by a validator. An analyzer that forbids a state the type system permits is a weaker guarantee
/// than a type that cannot hold it.
/// </remarks>
public interface IQueueBuilder
{
    /// <summary>
    /// Messages sharing a partition key run one at a time, oldest first; a failing head blocks
    /// <b>only its own partition</b>. Every message type bound to this lane must declare a partition
    /// selector, which is checked at startup.
    /// </summary>
    IOrderedQueueBuilder Ordered();

    /// <summary>
    /// No ordering: at most <paramref name="maxConcurrency"/> messages of this lane run at once.
    /// </summary>
    IConcurrentQueueBuilder Concurrent(int maxConcurrency);

    /// <summary>
    /// No ordering and no practical limit — a message runs whether or not its predecessor finished.
    /// </summary>
    /// <remarks>
    /// A distinct method rather than <c>Concurrent(int.MaxValue)</c> so the intent is greppable: this
    /// is the only mode in which overlapping the same logical work is deliberate.
    /// </remarks>
    IConcurrentQueueBuilder Unbounded();
}

/// <summary>An ordered lane. Note the absence of any concurrency-per-message knob.</summary>
public interface IOrderedQueueBuilder
{
    /// <summary>
    /// How to read a message's ordering domain — the build, pull request, repository or document that
    /// its messages are ordered <i>within</i>.
    /// </summary>
    /// <remarks>
    /// The selector runs once, producer-side, and the result is persisted, so it <b>must be pure</b>:
    /// nothing ever re-runs it, and a selector that changed its mind would split a partition in half
    /// invisibly. Declaring it here rather than at each broadcast means several producers of one
    /// message type cannot disagree about the key.
    /// </remarks>
    IOrderedQueueBuilder PartitionBy<TMessage>(Expression<Func<TMessage, string>> key);

    /// <summary>
    /// How many <b>distinct partitions</b> may run at once. This cannot violate ordering: each
    /// partition is still strictly sequential. It is not a per-message concurrency limit.
    /// </summary>
    IOrderedQueueBuilder MaxPartitionsInFlight(int partitions);

    IOrderedQueueBuilder Retry(IRetrySchedule schedule);

    /// <summary>
    /// Accepts a worst-case per-partition block longer than the global ceiling. Without this, a lane
    /// whose ladder sums past <c>MaxPartitionBlock</c> fails startup with the computed figure.
    /// </summary>
    IOrderedQueueBuilder AcceptPartitionBlock(TimeSpan budget);
}

/// <summary>An unordered lane. Note the absence of <c>PartitionBy</c>.</summary>
public interface IConcurrentQueueBuilder
{
    IConcurrentQueueBuilder Retry(IRetrySchedule schedule);
}
