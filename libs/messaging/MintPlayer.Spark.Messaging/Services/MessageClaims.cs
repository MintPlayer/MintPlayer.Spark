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
    public static readonly string NodeId = $"{Environment.MachineName}/{Guid.NewGuid():N}";

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
        catch (Raven.Client.Exceptions.ConcurrencyException)
        {
            return false;
        }
    }

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
}
