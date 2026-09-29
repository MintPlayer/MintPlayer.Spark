namespace MintPlayer.Spark.Messaging.Models;

public class SparkMessage
{
    public string? Id { get; set; }
    public string QueueName { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }

    /// <summary>
    /// The queue's <c>SparkQueuePriority</c> as a number (<c>-1</c> Low, <c>0</c> Normal, <c>1</c> High),
    /// stamped at publish (#460, M16b). Stored on the message because the single feeder's sorted query
    /// (<c>SparkMessages_ByPriority</c>) orders by it, and an index cannot read configuration. A priority
    /// changed in configuration therefore applies to messages published after the change.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// When the message joined the queue <b>by the database server's clock</b> (#460, M16b, spike S-M7):
    /// the <c>@last-modified</c> of the write that made it claimable, captured by the feeder on the first
    /// write it makes to the message (its claim or its throttle deferral), which costs no extra write.
    /// Null until then; the index reads <c>@last-modified</c> itself in the meantime, which is the same
    /// value because nothing else writes a claimable message before the feeder does.
    /// <para>
    /// Never the producer's <see cref="CreatedAtUtc"/>: ordering by the publisher's wall clock across
    /// instances is the design the decision register rejects. Captured once, so a retry, a reclaim or a
    /// throttle wake-up keeps the message's place instead of sending it to the back.
    /// </para>
    /// </summary>
    public DateTime? QueuedAtUtc { get; set; }

    /// <summary>
    /// Number of times this message has been picked up for processing (informational).
    /// Per-handler attempt counts are tracked in <see cref="Handlers"/>.
    /// </summary>
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; }
    public EMessageStatus Status { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// Publish-time deadline (<c>BroadcastOptions.ExpiresAtUtc</c>). Past it — or when the next retry
    /// or throttle slot would fall past it — the message is dead-lettered with
    /// <see cref="EDeadLetterReason.Expired"/> instead of being handled.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Set together with <see cref="EMessageStatus.DeadLettered"/>; null otherwise.</summary>
    public EDeadLetterReason? DeadLetterReason { get; set; }

    /// <summary>
    /// Clear <see cref="PayloadJson"/> once the message is terminal (<c>BroadcastOptions.ScrubPayloadOnTerminal</c>).
    /// </summary>
    public bool ScrubPayloadOnTerminal { get; set; }

    /// <summary>
    /// Per-handler execution state. Populated when the message is first picked up for processing.
    /// </summary>
    public List<HandlerExecution> Handlers { get; set; } = new();

    /// <summary>
    /// Set by <c>MessageRetrySweeper</c> when <see cref="NextAttemptAtUtc"/> has passed;
    /// cleared by the worker on pickup and when parking the message for another attempt.
    /// This is the subscription-visible redelivery gate: subscription where-clauses cannot
    /// evaluate time (<c>now()</c> silently never matches), so "the backoff has elapsed"
    /// must be materialized as plain field state by a component that CAN evaluate time.
    /// </summary>
    public bool WakeUp { get; set; }

    /// <summary>
    /// When the fallback sweeper last touched this message to trigger subscription
    /// re-evaluation (see <c>MessageRetrySweeper</c>). Informational.
    /// </summary>
    public DateTime? LastWakeUpUtc { get; set; }

    /// <summary>
    /// Identity of the process currently holding this message, set together with
    /// <see cref="EMessageStatus.Processing"/> and cleared when the message leaves that state.
    /// <para>
    /// This exists because <c>Processing</c> used to be written and read by <b>nothing</b>: not the
    /// subscription query, not the sweeper. A process that died between pickup and completion left
    /// the message at <c>Processing</c> for ever — no retry, no dead-letter, no log line — which is
    /// how a GitHub webhook could be accepted with a 200 and then silently never handled. GitHub
    /// does not re-deliver on its own, so that delivery was simply lost.
    /// </para>
    /// </summary>
    public string? OwnerId { get; set; }

    /// <summary>
    /// When the current <see cref="OwnerId"/>'s claim lapses. Past this instant the message is
    /// considered abandoned and <c>MessageRetrySweeper</c> returns it to
    /// <see cref="EMessageStatus.Pending"/> with <see cref="AttemptCount"/> incremented.
    /// <para>
    /// It is a wall-clock deadline rather than a liveness check, so it must comfortably exceed the
    /// slowest handler: reclaiming a message a live process is still working on causes duplicate
    /// processing, which is worse than reclaiming late. Renewed while a handler runs, so a genuinely
    /// slow handler does not lose its claim — see <c>MessagePump</c>.
    /// </para>
    /// </summary>
    public DateTime? ClaimExpiresAtUtc { get; set; }
}
