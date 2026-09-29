using MintPlayer.Spark.Messaging.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Taking and holding the claim on a message. A claim is the durable record that some process is
/// working on a message, and it is what makes <see cref="EMessageStatus.Processing"/> mean
/// something: before it existed, that status was written on pickup and read by nothing, so a host
/// that died mid-handler stranded the message for ever.
/// </summary>
internal static class MessageClaims
{
    /// <summary>
    /// Identity of this process for claim purposes: machine/pod name plus a per-process GUID.
    /// <para>
    /// The GUID matters. A restarted pod usually keeps its name, so name alone would let the new
    /// process mistake claims left by its own dead predecessor for its own, and process them
    /// concurrently with whatever the sweeper decides to do about them.
    /// </para>
    /// </summary>
    public static string NodeId => HostScope.Value ?? ProcessNodeId;

    private static readonly string ProcessNodeId = $"{Environment.MachineName}/{Guid.NewGuid():N}";

    /// <summary>
    /// A per-host identity for tests and spikes that run two messaging hosts in one process (S-M8,
    /// leader handover): set it before starting a host's services, and every task that host starts
    /// inherits it. Unset in an application, where one process is one host.
    /// </summary>
    internal static readonly AsyncLocal<string?> HostScope = new();

    /// <summary>
    /// Marks the message as claimed by <paramref name="ownerId"/> and saves immediately, before any
    /// handler runs. Returns false when another process claimed it first.
    /// </summary>
    /// <remarks>
    /// The caller must have opened <paramref name="session"/> with optimistic concurrency enabled
    /// (<see cref="UseOptimisticConcurrency"/>). Without it RavenDB's default is last-write-wins, and
    /// two feeders would both "successfully" claim the same message and both run its handlers.
    /// </remarks>
    public static async Task<bool> TryClaimAsync(
        IAsyncDocumentSession session,
        SparkMessage message,
        string ownerId,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        message.Status = EMessageStatus.Processing;
        message.OwnerId = ownerId;
        message.ClaimExpiresAtUtc = DateTime.UtcNow + ttl;
        message.AttemptCount++;
        // Consumed on pickup: left true, the message would keep matching the subscription query and
        // be redelivered in a tight loop.
        message.WakeUp = false;

        try
        {
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            // Someone else got there first. Not an error: the other holder will process it.
            return false;
        }
    }

    /// <summary>
    /// Pushes the claim's expiry out, so a genuinely slow handler does not have its message
    /// reclaimed underneath it. Returns false when the claim is no longer ours — the sweeper judged
    /// it abandoned and returned the message to the queue — which the caller must treat as "stop
    /// working on this message", since it is now scheduled for redelivery.
    /// </summary>
    public static async Task<bool> TryRenewAsync(
        IDocumentStore store,
        string messageId,
        string ownerId,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        // A conflict is not a lost claim: the owner's own handler-step saves write the same document,
        // and one landing between our load and save made the renewal fail. Returning false then read
        // as "finished" to the renewal loop, which stopped renewing for the rest of the handler's run.
        // Reload and re-check instead; ownership is what decides. Bounded, and without any wait.
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;

            var message = await session.LoadAsync<SparkMessage>(messageId, cancellationToken);
            if (message is null || message.OwnerId != ownerId || message.Status != EMessageStatus.Processing)
                return false;

            message.ClaimExpiresAtUtc = DateTime.UtcNow + ttl;

            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (Raven.Client.Exceptions.ConcurrencyException) when (attempt < attempts)
            {
            }
            catch (Raven.Client.Exceptions.ConcurrencyException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// After a failed renewal: whether the claim was really taken away, as opposed to the message
    /// having just been finished by its owner.
    /// </summary>
    /// <remarks>
    /// A renewal also fails when it lands after <see cref="MessageProcessor"/> saved the outcome but
    /// before the renewal was cancelled: the message is then Completed / Failed / DeadLettered with
    /// no owner. That is the normal end of processing, not a loss. A real loss leaves the message
    /// Pending (the sweeper requeued it) or Processing under another owner.
    /// </remarks>
    public static async Task<bool> WasReclaimedAsync(
        IDocumentStore store,
        string messageId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var message = await session.LoadAsync<SparkMessage>(messageId, cancellationToken);
        return message is not null
            && ((message.Status == EMessageStatus.Pending && !IsDeferredByThrottle(message))
                || (message.Status == EMessageStatus.Processing && message.OwnerId != ownerId));
    }

    /// <summary>
    /// Pending, unwoken and scheduled for later: the shape <see cref="DeferUnstartedAsync"/> leaves.
    /// The owner put it there itself, so it is not a reclaim. A sweeper reclaim always sets
    /// <see cref="SparkMessage.WakeUp"/>, which is what tells the two apart.
    /// </summary>
    private static bool IsDeferredByThrottle(SparkMessage message)
        => !message.WakeUp && message.NextAttemptAtUtc is { } next && next > DateTime.UtcNow;

    /// <summary>
    /// Releases a claim without deciding the message's fate, returning it to
    /// <see cref="EMessageStatus.Pending"/> so it is picked up again promptly. Used on graceful
    /// shutdown for messages that were claimed but never started: parking them for a backoff
    /// interval would be needlessly slow, and leaving them claimed would make the next host wait
    /// out the whole claim TTL before the sweeper reclaimed them.
    /// </summary>
    public static async Task ReleaseUnstartedAsync(
        IDocumentStore store,
        string messageId,
        string ownerId,
        CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var message = await session.LoadAsync<SparkMessage>(messageId, cancellationToken);
        if (message is null || message.OwnerId != ownerId)
            return;

        message.Status = EMessageStatus.Pending;
        message.OwnerId = null;
        message.ClaimExpiresAtUtc = null;
        message.WakeUp = true;
        // The pickup that never happened should not count against the retry budget.
        if (message.AttemptCount > 0)
            message.AttemptCount--;

        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The throttle's variant of <see cref="ReleaseUnstartedAsync"/>: gives up a claim on a message
    /// whose handlers have not started, and schedules it for <paramref name="nextAttemptAtUtc"/>.
    /// <para>
    /// Three differences, each required. It runs in the <b>caller's</b> session on the message the
    /// caller already loaded, so it is one write, not a load and a write. It leaves
    /// <see cref="SparkMessage.WakeUp"/> <b>false</b>, because the message must stay invisible to the
    /// subscription until the sweeper wakes it at its slot — <c>true</c> would redeliver it at once,
    /// in a tight loop. And the save is <b>optimistic</b>: if the sweeper reclaimed the message in the
    /// meantime, the reclaim wins and this returns false.
    /// </para>
    /// </summary>
    public static async Task<bool> DeferUnstartedAsync(
        IAsyncDocumentSession session,
        SparkMessage message,
        DateTime nextAttemptAtUtc,
        CancellationToken cancellationToken)
    {
        message.Status = EMessageStatus.Pending;
        message.OwnerId = null;
        message.ClaimExpiresAtUtc = null;
        message.WakeUp = false;
        message.NextAttemptAtUtc = nextAttemptAtUtc;
        // Not an attempt: nothing ran, so no retry budget is burned.
        if (message.AttemptCount > 0)
            message.AttemptCount--;

        // Forced for this one entity, leaving the session's own mode alone: the processor's session is
        // deliberately last-write-wins for the handler saves that follow a normal admission.
        // Re-storing a tracked entity with its own change vector forces the check for that entity only.
        await session.StoreAsync(message, session.Advanced.GetChangeVectorFor(message), message.Id, cancellationToken);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return false;
        }
    }
}
