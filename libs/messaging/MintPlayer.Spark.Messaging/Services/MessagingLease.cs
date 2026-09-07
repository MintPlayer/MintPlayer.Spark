using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// The cluster-wide claim on "this host runs the messaging feeder, the pumps and the sweeper".
/// </summary>
/// <param name="NodeId">Holder identity — machine/pod name plus a per-process GUID, so a restarted pod is a different holder than its own dead predecessor.</param>
/// <param name="AcquiredAtUtc">When this holder first took the lease.</param>
/// <param name="ExpiresAtUtc">When the lease lapses unless renewed.</param>
/// <param name="Fence">Monotonically increasing across handovers. Not used to fence side effects — see the remarks on <see cref="MessagingLeaseManager"/> — but it makes handovers visible in logs and diagnosable after the fact.</param>
public sealed record MessagingLease(string NodeId, DateTime AcquiredAtUtc, DateTime ExpiresAtUtc, long Fence);

/// <summary>
/// Acquires and renews the messaging lease, so that in a multi-instance deployment exactly one host
/// runs the singleton messaging machinery.
/// <para>
/// <b>The lease is a liveness mechanism, not a safety one, and that distinction is deliberate.</b>
/// Exclusivity on the message stream is enforced by RavenDB itself: the feeder opens its
/// subscription with <c>SubscriptionOpeningStrategy.WaitForFree</c>, and the server permits exactly
/// one connected worker per subscription. Measured on RavenDB 7.2 — a second worker blocks inside
/// <c>Run()</c> with no exception and no retry churn, and takes over in 962 ms after a graceful
/// close or 575 ms after the holder is killed outright. So two hosts cannot both feed even if both
/// believe they hold the lease.
/// </para>
/// <para>
/// What the lease buys is avoiding the pathologies of N hosts all trying: N sweepers patching the
/// same documents every interval, N index deployments flapping the same definitions, and a
/// standby's connection attempts filling the log. Because it is not load-bearing for correctness,
/// a lease bug degrades throughput rather than duplicating work — which is why there is no fencing
/// token on the side effects, and why a clock skew between hosts cannot cause double-processing.
/// </para>
/// </summary>
internal sealed partial class MessagingLeaseManager
{
    private const string LeaseKey = "spark/messaging/leader";

    [Inject] private readonly IDocumentStore documentStore;
    [Inject] private readonly ILogger<MessagingLeaseManager> logger;

    /// <summary>How long the lease is valid without renewal.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <summary>How often the holder renews. A third of the TTL, so two renewals may fail before the lease lapses.</summary>
    public static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(10);

    /// <summary>How often a standby checks whether the lease has become available.</summary>
    public static readonly TimeSpan StandbyPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether this host currently holds the lease. Maintained by
    /// <see cref="MessageSubscriptionManager"/> and read by the other messaging singletons —
    /// <see cref="MessageRetrySweeper"/> above all — so that N standby hosts do not all patch the
    /// same due messages every interval.
    /// <para>
    /// Reading it is advisory, never a safety gate: everything it guards is idempotent, so a
    /// standby that runs a sweep because of a stale read wastes writes rather than corrupting
    /// anything.
    /// </para>
    /// </summary>
    public bool IsHeld { get; internal set; }

    /// <summary>
    /// Attempts to take the lease. Succeeds when it is unheld, when it is already ours (a restart
    /// with the same identity, which cannot happen given the per-process GUID, but is harmless), or
    /// when the current holder's lease has expired.
    /// </summary>
    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var current = await GetAsync(cancellationToken);

        if (current is null)
        {
            // index 0: succeeds only if the key does not exist yet.
            var lease = new MessagingLease(MessageClaims.NodeId, now, now + Ttl, 1);
            var put = await documentStore.Operations.SendAsync(
                new PutCompareExchangeValueOperation<MessagingLease>(LeaseKey, lease, 0), token: cancellationToken);

            if (put.Successful)
                logger.LogInformation("Acquired the messaging lease (fence {Fence})", lease.Fence);

            return put.Successful;
        }

        if (current.Value.NodeId == MessageClaims.NodeId)
            return await TryRenewAsync(cancellationToken);

        if (current.Value.ExpiresAtUtc > now)
            return false; // Someone else holds a live lease.

        // Expired — take over, guarded on the index we read, so only one contender wins.
        var takeover = new MessagingLease(MessageClaims.NodeId, now, now + Ttl, current.Value.Fence + 1);
        var result = await documentStore.Operations.SendAsync(
            new PutCompareExchangeValueOperation<MessagingLease>(LeaseKey, takeover, current.Index), token: cancellationToken);

        if (result.Successful)
        {
            logger.LogInformation(
                "Took over the messaging lease from {PreviousNode}, whose lease expired at {ExpiredAt} (fence {Fence})",
                current.Value.NodeId, current.Value.ExpiresAtUtc, takeover.Fence);
        }

        return result.Successful;
    }

    /// <summary>
    /// Extends our lease. Returns false if it is no longer ours, which the caller must treat as
    /// eviction: tear the feeder and pumps down at once rather than keep running unleased.
    /// </summary>
    public async Task<bool> TryRenewAsync(CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        if (current is null)
            return false;

        // Guarded on holder identity as well as index. Without the NodeId check a renewal could
        // extend a lease another host had legitimately taken over, giving two hosts a lease that
        // each believes is theirs.
        if (current.Value.NodeId != MessageClaims.NodeId)
        {
            logger.LogWarning(
                "The messaging lease is now held by {Holder}; this host has been evicted", current.Value.NodeId);
            return false;
        }

        var renewed = current.Value with { ExpiresAtUtc = DateTime.UtcNow + Ttl };
        var result = await documentStore.Operations.SendAsync(
            new PutCompareExchangeValueOperation<MessagingLease>(LeaseKey, renewed, current.Index), token: cancellationToken);

        return result.Successful;
    }

    /// <summary>
    /// Releases the lease if it is still ours, so a standby can take over immediately instead of
    /// waiting out the TTL.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);

        // Guarded on identity: an unconditional delete would drop a lease another host had already
        // taken over, which is the bug the migration runner's release has.
        if (current is null || current.Value.NodeId != MessageClaims.NodeId)
            return;

        var deleted = await documentStore.Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<MessagingLease>(LeaseKey, current.Index), token: cancellationToken);

        if (deleted.Successful)
            logger.LogInformation("Released the messaging lease");
    }

    private async Task<CompareExchangeValue<MessagingLease>?> GetAsync(CancellationToken cancellationToken)
        => await documentStore.Operations.SendAsync(
            new GetCompareExchangeValueOperation<MessagingLease>(LeaseKey), token: cancellationToken);
}
