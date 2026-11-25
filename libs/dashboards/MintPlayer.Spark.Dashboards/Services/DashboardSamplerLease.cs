using System.Text.Json;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Dashboards.Services;

/// <summary>
/// The atomic value of the sampler lease (D29), stored as JSON text: who holds it, until when, and the last slot sampled.
/// </summary>
/// <param name="NodeId">Holder identity: machine name plus a per-instance GUID, so a restarted host is not its own dead predecessor.</param>
/// <param name="AcquiredAtUtc">When this holder first took the lease.</param>
/// <param name="ExpiresAtUtc">When the lease lapses unless the holder claims another slot.</param>
/// <param name="Fence">Increases on every handover; makes handovers visible after the fact.</param>
/// <param name="LastSlot">The last slot (<c>floor(utcNow / interval)</c>) any host sampled.</param>
internal sealed record DashboardSamplerLeaseValue(string NodeId, DateTime AcquiredAtUtc, DateTime ExpiresAtUtc, long Fence, long LastSlot);

/// <summary>
/// The claim on "this host samples slot N", one <see cref="ISparkAtomicValues"/> value for every instance that shares
/// the database (D29; #536 D45: on any store, RavenDB's compare-exchange among them).
/// </summary>
/// <remarks>
/// <para>
/// Modelled on Messaging's <c>MessagingLeaseManager</c> (holder identity, takeover only once the holder's lease has
/// expired, every write guarded on the version that was read, release guarded on identity), with the slot added: a host
/// writes slot N only after its compare-and-swap of <c>LastSlot</c> to N succeeds, so each slot is claimed once
/// across the instances even across a handover. S11 measured 0 duplicates over 78 contended slots.
/// </para>
/// <para>
/// A separate claim rather than Messaging's lease generalised: that one is a renewed leadership lease of a package
/// the dashboards must not depend on, and changing it would change a production lease for no gain to Messaging.
/// </para>
/// <para>
/// Not a safety mechanism on its own: appends use the slot-aligned timestamp, so even a double write of a slot would
/// overwrite the point instead of adding one (and a store's rollup skips a slot it has counted).
/// </para>
/// <para>
/// The key is <see cref="Key"/>, not the <c>spark/dashboards/sampler</c> compare-exchange value RavenDB applications
/// held before: that one is a JSON object, which the text values of <see cref="ISparkAtomicValues"/> cannot read.
/// <c>M_202611251400_DashboardsSamplerLeaseRetired</c> deletes it (#536 D46).
/// </para>
/// </remarks>
internal sealed class DashboardSamplerLease(ISparkAtomicValues values)
{
    public const string Key = "dashboards/sampler";

    /// <summary>This instance's identity.</summary>
    public string NodeId { get; } = $"{Environment.MachineName}/{Guid.NewGuid():N}";

    /// <summary>
    /// Claims <paramref name="slot"/>: succeeds when nobody else holds a live lease and no host sampled that slot yet.
    /// The lease is valid for <paramref name="ttl"/> from <paramref name="nowUtc"/>.
    /// </summary>
    public async Task<bool> TryClaimSlotAsync(long slot, DateTime nowUtc, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var current = await values.GetAsync(Key, cancellationToken);

        DashboardSamplerLeaseValue next;
        long version;
        if (current is null)
        {
            // version 0: succeeds only if the key does not exist yet.
            next = new DashboardSamplerLeaseValue(NodeId, nowUtc, nowUtc + ttl, 1, slot);
            version = 0;
        }
        else
        {
            if (Parse(current.Value) is { } value)
            {
                if (value.LastSlot >= slot)
                    return false; // this slot (or a later one) was sampled already
                if (value.NodeId != NodeId && value.ExpiresAtUtc > nowUtc)
                    return false; // someone else holds a live lease

                next = value.NodeId == NodeId
                    ? value with { ExpiresAtUtc = nowUtc + ttl, LastSlot = slot }
                    : new DashboardSamplerLeaseValue(NodeId, nowUtc, nowUtc + ttl, value.Fence + 1, slot);
            }
            else
            {
                // Unreadable: nobody can be holding it, so it is taken over like a lapsed lease.
                next = new DashboardSamplerLeaseValue(NodeId, nowUtc, nowUtc + ttl, 1, slot);
            }
            version = current.Version;
        }

        return await values.TryPutAsync(Key, JsonSerializer.Serialize(next), version, cancellationToken) is not null;
    }

    /// <summary>
    /// Gives the lease up if it is still this instance's, keeping <c>LastSlot</c>, so a standby takes the next slot at
    /// once instead of waiting out the TTL. Best effort: a failed release lapses at the TTL.
    /// </summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        var current = await values.GetAsync(Key, cancellationToken);
        if (current is null || Parse(current.Value) is not { } value || value.NodeId != NodeId)
            return;
        await values.TryPutAsync(Key, JsonSerializer.Serialize(value with { ExpiresAtUtc = DateTime.MinValue }), current.Version, cancellationToken);
    }

    /// <summary>The lease <paramref name="json"/> holds, or <see langword="null"/> when it is not one.</summary>
    internal static DashboardSamplerLeaseValue? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<DashboardSamplerLeaseValue>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
