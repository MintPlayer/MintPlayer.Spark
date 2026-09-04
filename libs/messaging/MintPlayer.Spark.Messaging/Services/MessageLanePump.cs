using System.Collections.Concurrent;
using System.Threading.Channels;
using MintPlayer.Spark.Messaging.Indexes;
using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Drains one lane, in order, without letting it block any other lane.
/// </summary>
/// <remarks>
/// <para>
/// <b>The doorbell carries no payload.</b> The feeder rings a capacity-one channel and returns; this
/// pump then works out for itself what to run, by query. An earlier design had the feeder hand over
/// message ids and drop them when a buffer filled — that is unsound, because a dropped message
/// returns <i>after</i> one enqueued behind it. Ringing a bell that is already ringing loses nothing.
/// </para>
/// <para>
/// <b>Order comes from a sort, not from arrival.</b> A message that fails is written back, which
/// bumps its change vector and moves it to the back of the subscription's delivery order — that is
/// exactly how the old implementation let a newer message overtake a retrying one. So arrival order
/// is not consulted: each pass queries the lane's backlog ordered by
/// <see cref="SparkMessage.Sequence"/>, and the first row seen for a partition <i>is</i> that
/// partition's head.
/// </para>
/// <para>
/// <b>Everything here is a cache of a fact in the database.</b> <c>inFlight</c> mirrors
/// <c>Status = Processing</c>; <c>parked</c> mirrors a future <c>NextAttemptAtUtc</c>. Drop this
/// object at any instant and correctness is unaffected — which is why crash recovery needs no
/// reconstruction pass, and why a field that would be authoritative only in memory does not belong
/// here.
/// </para>
/// </remarks>
internal sealed class MessageLanePump(
    LanePlan plan,
    IDocumentStore store,
    MessageProcessor processor,
    TimeProvider timeProvider,
    ILogger logger)
{
    private const int WindowSize = 256;

    /// <summary>How long the lane sleeps when it has nothing scheduled.</summary>
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a stop waits for handlers that were already running.</summary>
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Capacity one: a bell that is already ringing needs no second ring.</summary>
    private readonly Channel<bool> doorbell = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> parked = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim stateLock = new(1, 1);

    /// <summary>
    /// Handler work that has been started and not yet finished, so shutdown can wait for it.
    /// </summary>
    /// <remarks>
    /// Dispatch deliberately does not block the drain loop — that is what keeps one slow handler from
    /// stalling its lane's other partitions — so the work outlives the loop iteration that started
    /// it. Without tracking it, "the pump has stopped" would mean only "the loop exited", while
    /// handlers carried on running against a store the host is already tearing down.
    /// </remarks>
    private readonly ConcurrentDictionary<Task, byte> dispatched = new();

    private bool degraded;

    public string LaneName => plan.LaneName;

    /// <summary>Wakes the pump. Never blocks, never fails.</summary>
    public void Ring() => doorbell.Writer.TryWrite(true);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Lane '{Lane}' started ({Mode}, up to {InFlight} in flight, retry {Retry})",
            plan.LaneName, plan.Ordered ? "ordered" : "concurrent", plan.MaxInFlight, plan.Retry);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A drain failure must not kill the lane: the index may be mid-swap after a deploy,
                // in which case a query on a newly added field throws outright rather than waiting.
                logger.LogError(ex, "Lane '{Lane}' drain failed; retrying", plan.LaneName);
            }

            await WaitForWorkAsync(cancellationToken);
        }

        // The loop has exited, but handlers dispatched from it may still be running — they were
        // started deliberately without being awaited, so that a slow one cannot stall the lane. A
        // stop that returned here would be lying: the host would carry on disposing the document
        // store while those handlers were still querying it, which in tests means the database is
        // deleted underneath them and in production means work is abandoned mid-flight.
        await DrainInFlightAsync();
    }

    /// <summary>Waits for handler work already started, so "stopped" means stopped.</summary>
    private async Task DrainInFlightAsync()
    {
        var outstanding = dispatched.Keys.ToArray();
        if (outstanding.Length == 0)
            return;

        logger.LogInformation(
            "Lane '{Lane}' is stopping and waiting for {Count} in-flight message(s)", plan.LaneName, outstanding.Length);

        try
        {
            // Bounded: a handler that ignores cancellation must not hold shutdown open forever. The
            // wait is generous enough for an ordinary handler to notice the token and unwind.
            await Task.WhenAll(outstanding).WaitAsync(ShutdownDrainTimeout);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Lane '{Lane}' still had in-flight work after {Timeout}; abandoning the wait. A handler is "
                + "likely ignoring its CancellationToken.", plan.LaneName, ShutdownDrainTimeout);
        }
        catch (Exception ex)
        {
            // A handler's own failure is already recorded on its message; it must not turn shutdown
            // into an exception.
            logger.LogDebug(ex, "Lane '{Lane}' saw a fault while draining in-flight work", plan.LaneName);
        }
    }

    /// <summary>
    /// Sleeps until the doorbell rings or the earliest park comes due, whichever is first.
    /// </summary>
    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        // Two kinds of future work exist, and both must be able to wake the lane. A parked retry is
        // known in memory. A delayed broadcast is not — the drain filters it out server-side, on
        // purpose, so that it cannot occupy its partition's head — so its due time has to be asked
        // for. Without this the lane would only notice a delayed message on its next idle sweep.
        var wakeAt = Earliest(
            await NextParkDueAsync(cancellationToken),
            await NextVisibleAtAsync(cancellationToken));

        var idleFor = wakeAt is null
            ? IdleInterval
            : Min(Max(wakeAt.Value - timeProvider.GetUtcNow().UtcDateTime, TimeSpan.Zero), IdleInterval);

        using var timeout = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);

        // TimeProvider, not Task.Delay: tests drive backoff with a fake clock rather than sleeping
        // through a retry ladder in real time.
        var timer = timeProvider.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(),
            timeout, idleFor, Timeout.InfiniteTimeSpan);
        await using var _ = timer.ConfigureAwait(false);

        try
        {
            await doorbell.Reader.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Either the wait elapsed or we are shutting down; the loop decides which.
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        await stateLock.WaitAsync(cancellationToken);
        List<string> excluded;
        int capacity;
        try
        {
            ReleaseDuePartitions(now);
            excluded = [.. inFlight, .. parked.Keys];
            capacity = plan.MaxInFlight - inFlight.Count;
        }
        finally
        {
            stateLock.Release();
        }

        if (capacity <= 0)
            return;

        using var session = store.OpenAsyncSession();

        var query = session.Query<SparkMessage, SparkMessages_ByQueue>()
            // Load-bearing, not hygiene: without it the drain misses a just-written message about
            // nine times in ten, because the index has not caught up with the doorbell.
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
            .Where(m => m.QueueName == plan.LaneName
                     && (m.Status == EMessageStatus.Pending || m.Status == EMessageStatus.Failed)
                     && (m.VisibleAtUtc == null || m.VisibleAtUtc <= now));

        if (excluded.Count > 0)
        {
            // .In(), not !excluded.Contains(...): the latter does not compile in RavenDB's LINQ
            // provider, and this form is parameterized, so the RQL text stays a constant size
            // however many partitions are excluded.
            query = query.Where(m => !m.PartitionKey.In(excluded));
        }

        var window = await query
            // Sequence, never Id: ThenBy(Id) compiles to a lexicographic order by id(), and hilo
            // ids are not zero-padded, so /10-A sorts before /2-A.
            .OrderBy(m => m.Sequence)
            .Take(WindowSize)
            .ToListAsync(cancellationToken);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in window)
        {
            var partition = PartitionOf(message);

            // The first row of a partition is its head, unconditionally. The due-check is applied
            // HERE rather than in the query on purpose: filtering a parked head out server-side
            // would promote the next message of that partition to "first row", which is precisely
            // the overtake this design exists to prevent.
            if (!seen.Add(partition))
                continue;

            if (message.NextAttemptAtUtc > now)
            {
                await ParkAsync(partition, message.NextAttemptAtUtc.Value, now, cancellationToken);
                continue;
            }

            if (capacity <= 0)
                break;

            capacity--;
            await DispatchAsync(message.Id!, partition, cancellationToken);
        }
    }

    /// <summary>
    /// A concurrent lane is an ordered lane whose partition key is the message's own id — every
    /// message its own partition, so nothing is ordered and up to <c>MaxInFlight</c> run at once.
    /// </summary>
    private string PartitionOf(SparkMessage message)
        => plan.Ordered ? message.PartitionKey : message.Id!;

    private async Task DispatchAsync(string messageId, string partition, CancellationToken cancellationToken)
    {
        await stateLock.WaitAsync(cancellationToken);
        try
        {
            inFlight.Add(partition);
        }
        finally
        {
            stateLock.Release();
        }

        Task? work = null;
        work = Task.Run(async () =>
        {
            try
            {
                var outcome = await processor.ProcessAsync(messageId, plan.Retry, cancellationToken);

                await stateLock.WaitAsync(CancellationToken.None);
                try
                {
                    inFlight.Remove(partition);

                    if (!outcome.Terminal && outcome.NextAttemptAtUtc is { } due)
                        Park(partition, due, timeProvider.GetUtcNow().UtcDateTime);
                }
                finally
                {
                    stateLock.Release();
                }
            }
            catch (Exception ex)
            {
                // The processor is written not to throw; if it ever does, the partition must still
                // be released or the lane wedges permanently on a message it is no longer running.
                logger.LogError(ex, "Lane '{Lane}' failed to process {MessageId}", plan.LaneName, messageId);

                await stateLock.WaitAsync(CancellationToken.None);
                try { inFlight.Remove(partition); } finally { stateLock.Release(); }
            }
            finally
            {
                // Deregister before ringing, so a shutdown that observes the doorbell also observes
                // an empty in-flight set rather than racing this task's own removal.
                if (work is not null)
                    dispatched.TryRemove(work, out _);
            }

            Ring();
        }, CancellationToken.None);

        dispatched[work] = 0;

        // The task may already have finished and removed nothing, because it was registered after it
        // started. Re-check rather than leak a completed task into the shutdown wait.
        if (work.IsCompleted)
            dispatched.TryRemove(work, out _);
    }

    private async Task ParkAsync(string partition, DateTime dueUtc, DateTime now, CancellationToken cancellationToken)
    {
        await stateLock.WaitAsync(cancellationToken);
        try
        {
            Park(partition, dueUtc, now);
        }
        finally
        {
            stateLock.Release();
        }
    }

    /// <summary>Call with <see cref="stateLock"/> held.</summary>
    private void Park(string partition, DateTime dueUtc, DateTime now)
    {
        // Beyond the horizon, remembering the park buys nothing: the durable write already happened,
        // so the next drain rediscovers it from the document. Forgetting keeps a seven-day ladder
        // free of memory and of exclusion slots, which is what lets MaxParkedPartitions keep meaning
        // "this lane is failing fast right now".
        if (dueUtc - now > plan.ParkHorizon)
        {
            parked.Remove(partition);
            return;
        }

        parked[partition] = dueUtc;

        if (parked.Count > plan.MaxParkedPartitions && !degraded)
        {
            degraded = true;
            logger.LogError(
                "Lane '{Lane}' is degraded: {Parked} partitions are parked on a retry backoff, above the "
                + "{Max} threshold. This usually means a dependency is down rather than that individual "
                + "messages are bad.",
                plan.LaneName, parked.Count, plan.MaxParkedPartitions);
        }
    }

    /// <summary>Call with <see cref="stateLock"/> held.</summary>
    private void ReleaseDuePartitions(DateTime now)
    {
        foreach (var partition in parked.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            parked.Remove(partition);

        if (degraded && parked.Count <= plan.MaxParkedPartitions / 2)
        {
            degraded = false;
            logger.LogInformation("Lane '{Lane}' recovered: {Parked} partitions parked", plan.LaneName, parked.Count);
        }
    }

    private async Task<DateTime?> NextParkDueAsync(CancellationToken cancellationToken)
    {
        await stateLock.WaitAsync(cancellationToken);
        try
        {
            return parked.Count == 0 ? null : parked.Values.Min();
        }
        finally
        {
            stateLock.Release();
        }
    }

    /// <summary>The soonest a delayed broadcast on this lane becomes eligible, if any.</summary>
    private async Task<DateTime?> NextVisibleAtAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        using var session = store.OpenAsyncSession();
        var next = await session.Query<SparkMessage, SparkMessages_ByQueue>()
            .Where(m => m.QueueName == plan.LaneName
                     && m.Status == EMessageStatus.Pending
                     && m.VisibleAtUtc != null
                     && m.VisibleAtUtc > now)
            .OrderBy(m => m.VisibleAtUtc)
            .Select(m => m.VisibleAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return next;
    }

    private static DateTime? Earliest(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : (a < b ? a : b);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
