using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Inventory;

public sealed class InventoryEngineOptions
{
    /// <summary>How long after <c>ExpiresAt</c> a hold stays live, to absorb clock skew between nodes (I5).</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Attempts per operation before it gives up with <see cref="ClaimStatus.Conflict"/>.</summary>
    public int MaxAttempts { get; set; } = 10;
}

/// <summary>
/// The inventory engine (commerce PRD §4.3). Every operation is one RavenDB cluster-wide transaction over the
/// quota shards, the claim document and the compare-exchange values of units and buyer limits, so it succeeds or
/// fails as a whole (I2) and nothing is ever oversold (I1). Every operation is idempotent by claim id (I4) and
/// retried on a concurrency conflict, starting from another shard. Expiry is lazy: a hold stops counting at
/// <c>ExpiresAt + grace</c> whether or not a sweeper ran (I3, I5).
/// </summary>
public sealed class InventoryEngine(IDocumentStore store, string? database = null, InventoryEngineOptions? options = null, TimeProvider? clock = null)
{
    private readonly InventoryEngineOptions options = options ?? new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public static string ShardId(string quotaId, int k) => $"{quotaId}/shards/{k}";
    public static string UnitKey(string unitId) => $"inventory/units/{unitId}";
    public static string LimitKey(string key) => $"inventory/limits/{key}";

    private DateTimeOffset Now => clock.GetUtcNow();

    private bool IsLive(DateTimeOffset? expiresAt) => expiresAt is null || expiresAt.Value + options.Grace > Now;

    private IAsyncDocumentSession OpenSession() => store.OpenAsyncSession(new SessionOptions
    {
        Database = database,
        TransactionMode = TransactionMode.ClusterWide,
    });

    /// <summary>Creates a quota of <paramref name="capacity"/> units over <paramref name="shardCount"/> shards (the remainder goes to the first ones).</summary>
    public async Task CreateQuotaAsync(string quotaId, int capacity, int shardCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        using var session = OpenSession();
        await session.StoreAsync(new Quota { Id = quotaId, Capacity = capacity, ShardCount = shardCount }, cancellationToken);
        for (var k = 0; k < shardCount; k++)
        {
            var share = capacity / shardCount + (k < capacity % shardCount ? 1 : 0);
            await session.StoreAsync(new QuotaShard { Id = ShardId(quotaId, k), QuotaId = quotaId, CapacityShare = share }, cancellationToken);
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Holds everything in <paramref name="request"/> until <see cref="ClaimRequest.ExpiresAt"/>, or nothing.</summary>
    public Task<ClaimResult> TryClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
        => RetryAsync(attempt => ClaimOnceAsync(request, attempt, cancellationToken), cancellationToken);

    /// <summary>Moves a held claim's expiry (PaymentPending, D3). Fails with <see cref="ClaimStatus.Expired"/> once it lapsed.</summary>
    public Task<ClaimResult> ExtendAsync(string claimId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        => RetryAsync(_ => ExtendOnceAsync(claimId, expiresAt, cancellationToken), cancellationToken);

    /// <summary>Makes a held claim final: its units are sold. A payer converting their own hold gets the grace (I5).</summary>
    public Task<ClaimResult> ConvertAsync(string claimId, CancellationToken cancellationToken = default)
        => RetryAsync(_ => ConvertOnceAsync(claimId, cancellationToken), cancellationToken);

    /// <summary>
    /// Gives a claim back, exactly once. A held claim drops its holds; a converted one returns its quota units, numbered
    /// units and per-buyer counts — except that <paramref name="returnStock"/> false keeps the quota units sold (shipped
    /// goods come back through a restock, I4).
    /// </summary>
    public Task<ClaimResult> ReleaseAsync(string claimId, bool returnStock = true, CancellationToken cancellationToken = default)
        => RetryAsync(_ => ReleaseOnceAsync(claimId, returnStock, cancellationToken), cancellationToken);

    /// <summary>Prunes the quota's lapsed holds. Only display and housekeeping depend on it, never correctness (I3).</summary>
    public Task<ClaimResult> SweepAsync(string quotaId, CancellationToken cancellationToken = default)
        => RetryAsync(_ => SweepOnceAsync(quotaId, cancellationToken), cancellationToken);

    public async Task<QuotaAvailability> GetAvailabilityAsync(string quotaId, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession(database);
        var quota = await session.LoadAsync<Quota>(quotaId, cancellationToken)
            ?? throw new InvalidOperationException($"Quota '{quotaId}' does not exist.");
        var shards = await session.LoadAsync<QuotaShard>(Enumerable.Range(0, quota.ShardCount).Select(k => ShardId(quotaId, k)), cancellationToken);
        int capacity = 0, sold = 0, held = 0;
        foreach (var shard in shards.Values.OfType<QuotaShard>())
        {
            capacity += shard.CapacityShare;
            sold += shard.Sold;
            held += shard.Holds.Where(h => IsLive(h.ExpiresAt)).Sum(h => h.N);
        }
        return new QuotaAvailability(quotaId, capacity, sold, held, Math.Max(0, capacity - sold - held));
    }

    private async Task<ClaimResult> RetryAsync(Func<int, Task<ClaimResult>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(attempt);
            }
            catch (ConcurrencyException) when (attempt + 1 < options.MaxAttempts)
            {
                // Jittered backoff, growing with the attempt; the next attempt starts on another shard.
                await Task.Delay(Random.Shared.Next(1, 4 << Math.Min(attempt, 5)), cancellationToken);
            }
            catch (ConcurrencyException)
            {
                return new ClaimResult(ClaimStatus.Conflict);
            }
        }
    }

    private async Task<ClaimResult> ClaimOnceAsync(ClaimRequest request, int attempt, CancellationToken cancellationToken)
    {
        using var session = OpenSession();
        var existing = await session.LoadAsync<InventoryClaim>(request.ClaimId, cancellationToken);
        if (existing is not null)
            return existing.State == ClaimState.Held && IsLive(existing.ExpiresAt) ? ClaimResult.Ok : new ClaimResult(ClaimStatus.InvalidState);

        var claim = new InventoryClaim { Id = request.ClaimId, Owner = request.Owner, State = ClaimState.Held, ExpiresAt = request.ExpiresAt };

        foreach (var line in request.Quotas.GroupBy(l => l.QuotaId).Select(g => new QuotaLine(g.Key, g.Sum(l => l.N))))
        {
            var refused = await AllocateAsync(session, line, request, claim, attempt, cancellationToken);
            if (refused is not null) return refused;
        }

        foreach (var unitId in request.Units ?? [])
        {
            var key = UnitKey(unitId);
            var unit = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<UnitHold>(key, cancellationToken);
            if (unit is null)
                session.Advanced.ClusterTransaction.CreateCompareExchangeValue(key, new UnitHold { ClaimId = request.ClaimId, ExpiresAt = request.ExpiresAt });
            else if (!IsLive(unit.Value.ExpiresAt))
                unit.Value = new UnitHold { ClaimId = request.ClaimId, ExpiresAt = request.ExpiresAt };
            else
                return new ClaimResult(ClaimStatus.UnitTaken, unitId);
            claim.Units.Add(unitId);
        }

        foreach (var limit in request.Limits ?? [])
        {
            var key = LimitKey(limit.Key);
            var entry = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(key, cancellationToken);
            var count = entry?.Value ?? new BuyerCount();
            count.Holds.RemoveAll(h => !IsLive(h.ExpiresAt));
            if (count.Count + count.Holds.Sum(h => h.N) + limit.N > limit.Max)
                return new ClaimResult(ClaimStatus.LimitExceeded, limit.Key);
            count.Holds.Add(new Hold { ClaimId = request.ClaimId, N = limit.N, ExpiresAt = request.ExpiresAt });
            if (entry is null)
                session.Advanced.ClusterTransaction.CreateCompareExchangeValue(key, count);
            else
                entry.Value = count;
            claim.Limits.Add(new LimitAllocation { Key = limit.Key, N = limit.N });
        }

        await session.StoreAsync(claim, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return ClaimResult.Ok;
    }

    /// <summary>
    /// Takes <paramref name="line"/>'s units from one shard, starting from one chosen by claim id and attempt; when no
    /// single shard has room, splits them over all shards. Returns null on success.
    /// </summary>
    private async Task<ClaimResult?> AllocateAsync(IAsyncDocumentSession session, QuotaLine line, ClaimRequest request, InventoryClaim claim, int attempt, CancellationToken cancellationToken)
    {
        var quota = await session.LoadAsync<Quota>(line.QuotaId, cancellationToken);
        if (quota is null) return new ClaimResult(ClaimStatus.NotFound, line.QuotaId);

        var start = (int)((uint)(HashCode.Combine(request.ClaimId) + attempt * 7919) % (uint)quota.ShardCount);
        var first = await session.LoadAsync<QuotaShard>(ShardId(line.QuotaId, start), cancellationToken);
        if (first is not null && Room(first) >= line.N)
        {
            Take(first, line.N, request, claim);
            return null;
        }
        if (quota.ShardCount == 1) return new ClaimResult(ClaimStatus.SoldOut, line.QuotaId);

        var shards = await session.LoadAsync<QuotaShard>(Enumerable.Range(0, quota.ShardCount).Select(k => ShardId(line.QuotaId, (start + k) % quota.ShardCount)), cancellationToken);
        var ordered = shards.Values.OfType<QuotaShard>().ToList();
        if (ordered.Sum(Room) < line.N) return new ClaimResult(ClaimStatus.SoldOut, line.QuotaId);

        var remaining = line.N;
        foreach (var shard in ordered)
        {
            var n = Math.Min(Room(shard), remaining);
            if (n == 0) continue;
            Take(shard, n, request, claim);
            remaining -= n;
            if (remaining == 0) break;
        }
        return null;
    }

    private int Room(QuotaShard shard)
    {
        shard.Holds.RemoveAll(h => !IsLive(h.ExpiresAt));
        return shard.CapacityShare - shard.Sold - shard.Holds.Sum(h => h.N);
    }

    private static void Take(QuotaShard shard, int n, ClaimRequest request, InventoryClaim claim)
    {
        shard.Holds.Add(new Hold { ClaimId = request.ClaimId, N = n, ExpiresAt = request.ExpiresAt });
        claim.Allocations.Add(new ShardAllocation { ShardId = shard.Id!, N = n });
    }

    private async Task<ClaimResult> ExtendOnceAsync(string claimId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        using var session = OpenSession();
        var claim = await session.LoadAsync<InventoryClaim>(claimId, cancellationToken);
        if (claim is null) return new ClaimResult(ClaimStatus.NotFound, claimId);
        if (claim.State != ClaimState.Held) return new ClaimResult(ClaimStatus.InvalidState, claimId);
        if (!IsLive(claim.ExpiresAt)) return new ClaimResult(ClaimStatus.Expired, claimId);

        var refused = await VisitHoldsAsync(session, claim, (hold, _) => hold.ExpiresAt = expiresAt, unit => unit.ExpiresAt = expiresAt, cancellationToken);
        if (refused is not null) return refused;
        claim.ExpiresAt = expiresAt;
        await session.SaveChangesAsync(cancellationToken);
        return ClaimResult.Ok;
    }

    private async Task<ClaimResult> ConvertOnceAsync(string claimId, CancellationToken cancellationToken)
    {
        using var session = OpenSession();
        var claim = await session.LoadAsync<InventoryClaim>(claimId, cancellationToken);
        if (claim is null) return new ClaimResult(ClaimStatus.NotFound, claimId);
        if (claim.State == ClaimState.Converted) return ClaimResult.Ok;
        if (claim.State != ClaimState.Held) return new ClaimResult(ClaimStatus.InvalidState, claimId);
        if (!IsLive(claim.ExpiresAt)) return new ClaimResult(ClaimStatus.Expired, claimId);

        var refused = await VisitHoldsAsync(session, claim,
            (hold, owner) => owner.Remove(hold),
            unit => unit.ExpiresAt = null,
            cancellationToken,
            onShard: (shard, n) => shard.Sold += n,
            onLimit: (count, n) => count.Count += n);
        if (refused is not null) return refused;
        claim.State = ClaimState.Converted;
        claim.ExpiresAt = null;
        await session.SaveChangesAsync(cancellationToken);
        return ClaimResult.Ok;
    }

    private async Task<ClaimResult> ReleaseOnceAsync(string claimId, bool returnStock, CancellationToken cancellationToken)
    {
        using var session = OpenSession();
        var claim = await session.LoadAsync<InventoryClaim>(claimId, cancellationToken);
        if (claim is null) return new ClaimResult(ClaimStatus.NotFound, claimId);
        if (claim.State == ClaimState.Released) return ClaimResult.Ok;

        if (claim.State == ClaimState.Held)
        {
            // A lapsed hold may already be gone (pruned or taken over); whatever is still ours is dropped.
            foreach (var allocation in claim.Allocations)
            {
                var shard = await session.LoadAsync<QuotaShard>(allocation.ShardId, cancellationToken);
                shard?.Holds.RemoveAll(h => h.ClaimId == claimId);
            }
            foreach (var unitId in claim.Units)
            {
                var unit = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<UnitHold>(UnitKey(unitId), cancellationToken);
                if (unit is not null && unit.Value.ClaimId == claimId)
                    session.Advanced.ClusterTransaction.DeleteCompareExchangeValue(unit);
            }
            foreach (var limit in claim.Limits)
            {
                var entry = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(LimitKey(limit.Key), cancellationToken);
                entry?.Value.Holds.RemoveAll(h => h.ClaimId == claimId);
            }
        }
        else
        {
            if (returnStock)
            {
                foreach (var allocation in claim.Allocations)
                {
                    var shard = await session.LoadAsync<QuotaShard>(allocation.ShardId, cancellationToken)
                        ?? throw new InvalidOperationException($"Shard '{allocation.ShardId}' of converted claim '{claimId}' is missing.");
                    shard.Sold -= allocation.N;
                }
            }
            // A numbered unit (a seat) is never shipped: it always comes back.
            foreach (var unitId in claim.Units)
            {
                var unit = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<UnitHold>(UnitKey(unitId), cancellationToken);
                if (unit is not null && unit.Value.ClaimId == claimId)
                    session.Advanced.ClusterTransaction.DeleteCompareExchangeValue(unit);
            }
            foreach (var limit in claim.Limits)
            {
                var entry = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(LimitKey(limit.Key), cancellationToken);
                if (entry is not null) entry.Value.Count = Math.Max(0, entry.Value.Count - limit.N);
            }
        }

        claim.State = ClaimState.Released;
        claim.ExpiresAt = null;
        await session.SaveChangesAsync(cancellationToken);
        return ClaimResult.Ok;
    }

    /// <summary>
    /// Applies <paramref name="onHold"/> to each of the claim's holds and <paramref name="onUnit"/> to each unit; fails
    /// with <see cref="ClaimStatus.Expired"/> when any of them is no longer the claim's.
    /// </summary>
    private async Task<ClaimResult?> VisitHoldsAsync(
        IAsyncDocumentSession session,
        InventoryClaim claim,
        Action<Hold, List<Hold>> onHold,
        Action<UnitHold> onUnit,
        CancellationToken cancellationToken,
        Action<QuotaShard, int>? onShard = null,
        Action<BuyerCount, int>? onLimit = null)
    {
        foreach (var group in claim.Allocations.GroupBy(a => a.ShardId))
        {
            var shard = await session.LoadAsync<QuotaShard>(group.Key, cancellationToken);
            var hold = shard?.Holds.FirstOrDefault(h => h.ClaimId == claim.Id);
            if (shard is null || hold is null) return new ClaimResult(ClaimStatus.Expired, claim.Id);
            onHold(hold, shard.Holds);
            onShard?.Invoke(shard, hold.N);
        }
        foreach (var unitId in claim.Units)
        {
            var unit = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<UnitHold>(UnitKey(unitId), cancellationToken);
            if (unit is null || unit.Value.ClaimId != claim.Id) return new ClaimResult(ClaimStatus.Expired, unitId);
            onUnit(unit.Value);
        }
        foreach (var limit in claim.Limits)
        {
            var entry = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(LimitKey(limit.Key), cancellationToken);
            var hold = entry?.Value.Holds.FirstOrDefault(h => h.ClaimId == claim.Id);
            if (entry is null || hold is null) return new ClaimResult(ClaimStatus.Expired, limit.Key);
            onHold(hold, entry.Value.Holds);
            onLimit?.Invoke(entry.Value, hold.N);
        }
        return null;
    }

    private async Task<ClaimResult> SweepOnceAsync(string quotaId, CancellationToken cancellationToken)
    {
        using var session = OpenSession();
        var quota = await session.LoadAsync<Quota>(quotaId, cancellationToken);
        if (quota is null) return new ClaimResult(ClaimStatus.NotFound, quotaId);
        var shards = await session.LoadAsync<QuotaShard>(Enumerable.Range(0, quota.ShardCount).Select(k => ShardId(quotaId, k)), cancellationToken);
        var pruned = 0;
        foreach (var shard in shards.Values.OfType<QuotaShard>())
            pruned += shard.Holds.RemoveAll(h => !IsLive(h.ExpiresAt));
        if (pruned > 0)
            await session.SaveChangesAsync(cancellationToken);
        return ClaimResult.Ok;
    }
}
