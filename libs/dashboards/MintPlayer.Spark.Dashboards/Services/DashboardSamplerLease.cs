using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Dashboards.Services;

/// <summary>
/// The compare-exchange value of the sampler lease (D29): who holds it, until when, and the last slot sampled.
/// </summary>
/// <param name="NodeId">Holder identity: machine name plus a per-instance GUID, so a restarted host is not its own dead predecessor.</param>
/// <param name="AcquiredAtUtc">When this holder first took the lease.</param>
/// <param name="ExpiresAtUtc">When the lease lapses unless the holder claims another slot.</param>
/// <param name="Fence">Increases on every handover; makes handovers visible after the fact.</param>
/// <param name="LastSlot">The last slot (<c>floor(utcNow / interval)</c>) any host sampled.</param>
internal sealed record DashboardSamplerLeaseValue(string NodeId, DateTime AcquiredAtUtc, DateTime ExpiresAtUtc, long Fence, long LastSlot);

/// <summary>
/// The claim on "this host samples slot N", one compare-exchange value for the cluster (D29).
/// </summary>
/// <remarks>
/// <para>
/// Modelled on Messaging's <c>MessagingLeaseManager</c> (holder identity, takeover only once the holder's lease has
/// expired, every write guarded on the index that was read, release guarded on identity), with the slot added: a host
/// writes slot N only after its compare-and-swap of <c>LastSlot</c> to N succeeds, so each slot is claimed once
/// cluster-wide even across a handover. S11 measured 0 duplicates over 78 contended slots.
/// </para>
/// <para>
/// A separate claim rather than Messaging's lease generalised: that one is a renewed leadership lease of a package
/// the dashboards must not depend on, and moving it into a shared assembly would put a RavenDB dependency into the
/// RavenDB-free Abstractions (or add a package) and change a production lease for no gain to Messaging.
/// </para>
/// <para>
/// Not a safety mechanism on its own: appends use the slot-aligned timestamp, so even a double write of a slot would
/// overwrite the point instead of adding one.
/// </para>
/// </remarks>
internal sealed class DashboardSamplerLease(IDocumentStore store)
{
    public const string Key = "spark/dashboards/sampler";

    /// <summary>This instance's identity.</summary>
    public string NodeId { get; } = $"{Environment.MachineName}/{Guid.NewGuid():N}";

    /// <summary>
    /// Claims <paramref name="slot"/>: succeeds when nobody else holds a live lease and no host sampled that slot yet.
    /// The lease is valid for <paramref name="ttl"/> from <paramref name="nowUtc"/>.
    /// </summary>
    public async Task<bool> TryClaimSlotAsync(long slot, DateTime nowUtc, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var current = await store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<DashboardSamplerLeaseValue>(Key), token: cancellationToken);

        DashboardSamplerLeaseValue next;
        long index;
        if (current is null)
        {
            // index 0: succeeds only if the key does not exist yet.
            next = new DashboardSamplerLeaseValue(NodeId, nowUtc, nowUtc + ttl, 1, slot);
            index = 0;
        }
        else
        {
            var value = current.Value;
            if (value.LastSlot >= slot)
                return false; // this slot (or a later one) was sampled already
            if (value.NodeId != NodeId && value.ExpiresAtUtc > nowUtc)
                return false; // someone else holds a live lease

            next = value.NodeId == NodeId
                ? value with { ExpiresAtUtc = nowUtc + ttl, LastSlot = slot }
                : new DashboardSamplerLeaseValue(NodeId, nowUtc, nowUtc + ttl, value.Fence + 1, slot);
            index = current.Index;
        }

        var result = await store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DashboardSamplerLeaseValue>(Key, next, index), token: cancellationToken);
        return result.Successful;
    }

    /// <summary>
    /// Gives the lease up if it is still this instance's, keeping <c>LastSlot</c>, so a standby takes the next slot at
    /// once instead of waiting out the TTL. Best effort: a failed release lapses at the TTL.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        var current = await store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<DashboardSamplerLeaseValue>(Key), token: cancellationToken);
        if (current is null || current.Value.NodeId != NodeId)
            return;
        await store.Operations.SendAsync(
            new PutCompareExchangeValueOperation<DashboardSamplerLeaseValue>(Key, current.Value with { ExpiresAtUtc = DateTime.MinValue }, current.Index),
            token: cancellationToken);
    }
}
