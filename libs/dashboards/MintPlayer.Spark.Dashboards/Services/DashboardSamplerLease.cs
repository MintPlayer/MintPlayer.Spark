using Raven.Client.Documents;
using MintPlayer.Spark.Claims;

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
/// Built on Spark's claims primitive (<see cref="ISparkClaims"/>, commerce X12), like Messaging's lease: holder
/// identity, takeover only once the holder's lease has expired, every write guarded on the index that was read,
/// release guarded on identity. The slot is added on top: a host writes slot N only after its compare-and-swap of
/// <c>LastSlot</c> to N succeeds, so each slot is claimed once cluster-wide even across a handover. S11 measured 0
/// duplicates over 78 contended slots (before the move onto the primitive, which keeps the same compare-and-swap).
/// </para>
/// <para>
/// Its value keeps its own shape (<see cref="DashboardSamplerLeaseValue"/>), so the stored lease needs no migration.
/// </para>
/// <para>
/// Not a safety mechanism on its own: appends use the slot-aligned timestamp, so even a double write of a slot would
/// overwrite the point instead of adding one.
/// </para>
/// </remarks>
internal sealed class DashboardSamplerLease(IDocumentStore store)
{
    public const string Key = "spark/dashboards/sampler";

    /// <summary>The claim family (commerce X12): one key, owned by a node, expiring at the lease's <c>ExpiresAtUtc</c>.</summary>
    private static readonly SparkClaimKind<DashboardSamplerLeaseValue> Kind = new("spark/dashboards/", lease => lease.NodeId)
    {
        ExpiresAtUtcOf = lease => lease.ExpiresAtUtc,
    };

    private static readonly SparkClaimKey ClaimKey = Kind.Key("sampler");

    private readonly SparkClaims claims = new(store);

    /// <summary>This instance's identity.</summary>
    public string NodeId { get; } = $"{Environment.MachineName}/{Guid.NewGuid():N}";

    /// <summary>
    /// Claims <paramref name="slot"/>: succeeds when nobody else holds a live lease and no host sampled that slot yet.
    /// The lease is valid for <paramref name="ttl"/> from <paramref name="nowUtc"/>.
    /// </summary>
    public async Task<bool> TryClaimSlotAsync(long slot, DateTime nowUtc, TimeSpan ttl, CancellationToken cancellationToken)
    {
        // A free key, or another node's expired lease: taken (over) with the slot. The previous value's LastSlot is
        // honoured across the handover, so a slot is claimed once cluster-wide.
        var reserved = await claims.ReserveAsync(Kind, ClaimKey, NodeId,
            previous => previous switch
            {
                null => new DashboardSamplerLeaseValue(NodeId, nowUtc, nowUtc + ttl, 1, slot),
                { Value.LastSlot: var last } when last >= slot => null, // this slot (or a later one) was sampled already
                _ => new DashboardSamplerLeaseValue(NodeId, nowUtc, nowUtc + ttl, previous!.Value.Fence + 1, slot),
            },
            nowUtc, cancellationToken);
        if (reserved.Status == SparkClaimStatus.Reserved)
            return true;
        if (reserved.Status != SparkClaimStatus.AlreadyYours)
            return false; // someone else holds a live lease, the slot was sampled, or another node won the race

        // Our own lease: renewed with the slot, unless that slot (or a later one) was sampled already.
        var renewed = await claims.UpdateAsync(Kind, ClaimKey, NodeId,
            lease => lease.LastSlot >= slot ? null : lease with { ExpiresAtUtc = nowUtc + ttl, LastSlot = slot },
            cancellationToken);
        return renewed.Status == SparkClaimStatus.Updated;
    }

    /// <summary>
    /// Gives the lease up if it is still this instance's, keeping <c>LastSlot</c>, so a standby takes the next slot at
    /// once instead of waiting out the TTL. Best effort: a failed release lapses at the TTL.
    /// </summary>
    public Task ReleaseAsync(CancellationToken cancellationToken)
        => claims.UpdateAsync(Kind, ClaimKey, NodeId, lease => lease with { ExpiresAtUtc = DateTime.MinValue }, cancellationToken);
}
