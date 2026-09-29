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
    /// The failure bound on <see cref="ReleaseAsync"/>: two small compare-exchange requests, which
    /// take milliseconds against a reachable store. It bounds a stop against an unreachable one; it
    /// is never waited out on the normal path.
    /// </summary>
    public static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Releases the lease if it is still ours, so a standby can take over immediately instead of
    /// waiting out the TTL.
    /// <para>
    /// Deliberately not cancellable by the caller. Its callers are stopping, and a stop token is
    /// often cancelled already (the host's shutdown timeout, or the stopping token itself), which
    /// would cancel the release before it reached RavenDB and leave every standby waiting out the
    /// TTL. It runs on its own token, bounded by <see cref="ReleaseTimeout"/> instead. A release
    /// that cannot happen — the store is disposed or unreachable — is expected, not an error: the
    /// lease simply lapses at its TTL, which is the crash path the standby already handles.
    /// </para>
    /// </summary>
    /// <returns>Whether the lease was released by this call.</returns>
    public async Task<bool> ReleaseAsync()
    {
        // A disposed store cannot send anything: RavenDB throws OperationCanceledException from its
        // context pool (or ObjectDisposedException), which is what S-M8's crashed host logged as
        // "Error releasing the messaging lease" on teardown.
        if (documentStore.WasDisposed)
        {
            logger.LogDebug("Not releasing the messaging lease: the document store is disposed; it lapses at its TTL");
            return false;
        }

        using var bound = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            var current = await GetAsync(bound.Token);

            // Guarded on identity: an unconditional delete would drop a lease another host had already
            // taken over, which is the bug the migration runner's release has.
            if (current is null || current.Value.NodeId != MessageClaims.NodeId)
                return false;

            var deleted = await documentStore.Operations.SendAsync(
                new DeleteCompareExchangeValueOperation<MessagingLease>(LeaseKey, current.Index), token: bound.Token);

            if (deleted.Successful)
                logger.LogInformation("Released the messaging lease (fence {Fence})", current.Value.Fence);

            return deleted.Successful;
        }
        catch (Exception ex) when (documentStore.WasDisposed && ex is OperationCanceledException or ObjectDisposedException)
        {
            logger.LogDebug("Not releasing the messaging lease: the document store was disposed during the release; it lapses at its TTL");
            return false;
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested)
        {
            logger.LogWarning(
                "Releasing the messaging lease did not complete within {Timeout}; it lapses at its TTL ({Ttl}), "
                + "so a standby takes over after that instead of at once", ReleaseTimeout, Ttl);
            return false;
        }
    }

    private async Task<CompareExchangeValue<MessagingLease>?> GetAsync(CancellationToken cancellationToken)
        => await documentStore.Operations.SendAsync(
            new GetCompareExchangeValueOperation<MessagingLease>(LeaseKey), token: cancellationToken);
}
