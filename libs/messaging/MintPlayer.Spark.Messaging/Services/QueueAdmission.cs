using System.Collections.Concurrent;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Per-queue admission control: GCRA-style reserved start slots for <c>MaxPerInterval</c> /
/// <c>Interval</c>, plus the <c>BatchSize</c> / <c>MinDelayBetweenBatches</c> pacing window.
/// <para>
/// <b>Reserved, not re-polled.</b> A message over budget is given the next free slot and that slot is
/// taken for it, so the next message gets the slot after. When the deferred message comes back (the
/// sweeper wakes it at its <c>NextAttemptAtUtc</c>), its reservation is found and it is admitted
/// without asking for a new one. Each throttled message is therefore deferred exactly once; a design
/// that re-checked a bucket on every return would defer the n-th message n times.
/// </para>
/// <para>
/// <b>In memory is enough.</b> The leader lease gates the feeder and the per-queue workers in both
/// modes, so one process admits for every queue at a time. A restart forgets the reservations; the
/// messages already deferred then reserve again and are deferred once more — a delay, never a burst,
/// because a fresh state admits at most one interval's budget.
/// </para>
/// </summary>
internal sealed class QueueAdmission
{
    /// <summary>A reservation older than this is for a message that will not come back (deleted, expired).</summary>
    private static readonly TimeSpan ReservationHorizon = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, QueueState> queues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> reservations = new(StringComparer.Ordinal);
    private long decisions;

    /// <summary>Outstanding reservations. For tests and diagnostics.</summary>
    internal int ReservationCount => reservations.Count;

    /// <summary>
    /// Decides whether <paramref name="messageId"/> may start now.
    /// </summary>
    /// <returns>
    /// <see cref="AdmissionKind.Admit"/>; <see cref="AdmissionKind.Defer"/> with the reserved slot; or
    /// <see cref="AdmissionKind.Expired"/> when the slot would fall after <paramref name="expiresAtUtc"/>
    /// (no slot is consumed then).
    /// </returns>
    public AdmissionDecision Decide(
        string queueName, string messageId, SparkQueueOptions? options, DateTime now, DateTime? expiresAtUtc)
    {
        if (options is null || !options.IsThrottled)
            return AdmissionDecision.Admit;

        if (Interlocked.Increment(ref decisions) % 1024 == 0)
            PruneStale(now);

        // Coming back for a slot reserved earlier: that slot is already paid for.
        if (reservations.TryGetValue(messageId, out var reserved))
        {
            if (now >= reserved)
            {
                reservations.TryRemove(messageId, out _);
                return AdmissionDecision.Admit;
            }

            // Early (a replayed delivery): same slot, no new reservation.
            return AdmissionDecision.Defer(reserved);
        }

        var state = queues.GetOrAdd(queueName, static _ => new QueueState());
        DateTime slot;
        lock (state)
        {
            slot = state.NextSlot(options, now);
            if (expiresAtUtc is { } expires && slot > expires)
                return AdmissionDecision.Expired;
            state.Commit(options, slot);
        }

        if (slot <= now)
            return AdmissionDecision.Admit;

        reservations[messageId] = slot;
        return AdmissionDecision.Defer(slot);
    }

    /// <summary>Forgets a reservation whose message will not start (dead-lettered, deleted).</summary>
    public void Forget(string messageId) => reservations.TryRemove(messageId, out _);

    private void PruneStale(DateTime now)
    {
        foreach (var (id, slot) in reservations)
        {
            if (slot < now - ReservationHorizon)
                reservations.TryRemove(id, out _);
        }
    }

    private sealed class QueueState
    {
        /// <summary>GCRA theoretical arrival time: when the bucket is next fully "on schedule".</summary>
        private DateTime tat = DateTime.MinValue;

        private int batchCount;
        private DateTime lastSlot = DateTime.MinValue;

        public DateTime NextSlot(SparkQueueOptions options, DateTime now)
        {
            var slot = now;

            if (options.MaxPerInterval > 0 && options.Interval > TimeSpan.Zero)
            {
                // Emission interval T and burst tolerance tau = Interval - T: up to MaxPerInterval
                // may start together, and after that one every T.
                var emission = options.Interval / options.MaxPerInterval;
                var tolerance = options.Interval - emission;
                var allowedAt = tat == DateTime.MinValue ? now : tat - tolerance;
                if (allowedAt > slot) slot = allowedAt;
            }

            if (options.BatchSize > 0 && lastSlot != DateTime.MinValue)
            {
                // A full batch pushes the next start past the pause; a batch that still has room is
                // joined, never started early — slots only move forward.
                var earliest = batchCount >= options.BatchSize ? lastSlot + options.MinDelayBetweenBatches : lastSlot;
                if (earliest > slot) slot = earliest;
            }

            return slot;
        }

        public void Commit(SparkQueueOptions options, DateTime slot)
        {
            if (options.MaxPerInterval > 0 && options.Interval > TimeSpan.Zero)
            {
                var emission = options.Interval / options.MaxPerInterval;
                tat = (tat > slot ? tat : slot) + emission;
            }

            if (options.BatchSize > 0)
            {
                // A new batch starts when the previous one is full, or when a natural gap at least as
                // long as the pause has already passed.
                if (batchCount >= options.BatchSize
                    || (lastSlot != DateTime.MinValue && slot >= lastSlot + options.MinDelayBetweenBatches))
                    batchCount = 0;

                batchCount++;
            }

            if (slot > lastSlot) lastSlot = slot;
        }
    }
}

internal enum AdmissionKind { Admit, Defer, Expired }

internal readonly record struct AdmissionDecision(AdmissionKind Kind, DateTime Slot)
{
    public static readonly AdmissionDecision Admit = new(AdmissionKind.Admit, default);
    public static readonly AdmissionDecision Expired = new(AdmissionKind.Expired, default);
    public static AdmissionDecision Defer(DateTime slot) => new(AdmissionKind.Defer, slot);
}
