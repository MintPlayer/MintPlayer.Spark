using MintPlayer.Spark.Claims;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Inventory;

public sealed class InventoryEngineOptions
{
    /// <summary>How long after <c>ExpiresAt</c> a hold stays live, to absorb clock skew between nodes (I5). S3: 2 s keeps lapsed holds visible as available within 5 s.</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Attempts per operation before it gives up with <see cref="ClaimStatus.Conflict"/>.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>The single-empty-seat rule for picked seats, unless a <see cref="SeatPick"/> names its own.</summary>
    public SingleSeatRule SingleSeatRule { get; set; } = SingleSeatRule.BetweenTakenOrRowEnd;
}

/// <summary>
/// The inventory engine (commerce PRD §4.3): see <see cref="ISparkInventory"/>. Every operation runs in one X12 claims
/// unit of work (<see cref="ISparkClaims.BeginUnitOfWork"/>): a RavenDB cluster-wide session in which the quota
/// shards, chains and claim documents are guarded documents and the seats are compare-exchange claims of the kind
/// <c>commerce/units/</c>, so the whole operation commits or nothing does (I2). Per-buyer limits are compare-exchange
/// values of their own (<c>commerce/limits/</c>): a counter shared by many claims, which X12's single-owner claim does
/// not model, written in the same transaction.
/// </summary>
/// <remarks>
/// S18: cluster-wide sessions refuse patches, counters and optimistic concurrency, and a document written by an
/// ordinary session breaks the next guarded write. So only this class writes the engine's documents.
/// </remarks>
public sealed class InventoryEngine : ISparkInventory
{
    public const string UnitPrefix = "commerce/units/";
    public const string LimitPrefix = "commerce/limits/";

    private static readonly ClaimResult ConflictResult = new(ClaimStatus.Conflict);

    private readonly IDocumentStore store;
    private readonly string? database;
    private readonly InventoryEngineOptions options;
    private readonly TimeProvider clock;
    private readonly SparkClaims claims;
    private readonly SparkClaimKind<SparkClaimHold> unitKind;
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task>? committed;

    /// <param name="store">The store.</param>
    /// <param name="database">The database; default the store's.</param>
    /// <param name="options">Grace, attempts, the single-seat rule.</param>
    /// <param name="clock">The clock expiry is judged by.</param>
    /// <param name="committed">Called after a commit with the quotas whose availability it changed (live topics, X2).</param>
    public InventoryEngine(
        IDocumentStore store,
        string? database = null,
        InventoryEngineOptions? options = null,
        TimeProvider? clock = null,
        Func<IReadOnlyCollection<string>, CancellationToken, Task>? committed = null)
    {
        this.store = store;
        this.database = database;
        this.options = options ?? new();
        this.clock = clock ?? TimeProvider.System;
        this.committed = committed;
        claims = new SparkClaims(store, this.clock, database);
        unitKind = SparkClaimKinds.Holds(UnitPrefix, this.options.Grace);
    }

    public static string ShardId(string quotaId, int k) => $"{quotaId}/shards/{k}";
    public static string ItemDocumentId(string itemId) => $"inventory-items/{itemId}";
    public static string UnitKey(string unitId) => UnitPrefix + unitId;
    public static string LimitKey(string key) => LimitPrefix + key;

    private DateTimeOffset Now => clock.GetUtcNow();

    private bool IsLive(DateTimeOffset? expiresAt) => expiresAt is null || expiresAt.Value + options.Grace > Now;

    private bool IsLive(SparkClaimKey key, CompareExchangeValue<SparkClaimHold>? value)
        => value?.Value is { } hold && unitKind.IsLiveAt(new SparkClaim<SparkClaimHold>(key, hold, value.Index), Now.UtcDateTime);

    private IAsyncDocumentSession OpenReadSession() => store.OpenAsyncSession(database);

    // ---- definitions ---------------------------------------------------------------------------------------

    public async Task CreateQuotaAsync(string quotaId, int capacity, int shardCount, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(quotaId);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);
        var result = await RetryAsync(async _ =>
        {
            using var uow = claims.BeginUnitOfWork();
            var session = uow.Session;
            if (await session.LoadAsync<InventoryQuota>(quotaId, cancellationToken) is not null)
                return ClaimResult.Ok;
            await session.StoreAsync(new InventoryQuota { Id = quotaId, Capacity = capacity, ShardCount = shardCount }, cancellationToken);
            for (var k = 0; k < shardCount; k++)
            {
                var share = capacity / shardCount + (k < capacity % shardCount ? 1 : 0);
                await session.StoreAsync(new InventoryQuotaShard { Id = ShardId(quotaId, k), QuotaId = quotaId, CapacityShare = share }, cancellationToken);
            }
            return await CommitAsync(uow, ClaimResult.Ok, [quotaId], cancellationToken);
        }, cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Quota '{quotaId}' could not be created: {result.Status}.");
    }

    public Task<ClaimResult> SetCapacityAsync(string quotaId, int capacity, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        return RetryAsync(async _ =>
        {
            using var uow = claims.BeginUnitOfWork();
            var session = uow.Session;
            var quota = await session.LoadAsync<InventoryQuota>(quotaId, cancellationToken);
            if (quota is null) return new ClaimResult(ClaimStatus.NotFound, quotaId);
            var shards = await LoadShardsAsync(session, quota, cancellationToken);
            var committedPerShard = shards.Select(s => s.CapacityShare - Room(s)).ToArray();
            var total = committedPerShard.Sum();
            if (capacity < total) return new ClaimResult(ClaimStatus.InvalidState, quotaId);
            var extra = capacity - total;
            for (var k = 0; k < shards.Count; k++)
                shards[k].CapacityShare = committedPerShard[k] + extra / shards.Count + (k < extra % shards.Count ? 1 : 0);
            quota.Capacity = capacity;
            return await CommitAsync(uow, ClaimResult.Ok, [quotaId], cancellationToken);
        }, cancellationToken);
    }

    public async Task MapItemAsync(string itemId, IReadOnlyList<ItemQuota> quotas, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(itemId);
        if (quotas.Any(q => q.Multiplier < 1 || string.IsNullOrEmpty(q.QuotaId)))
            throw new ArgumentException("Every mapping names a quota and a multiplier of at least 1.", nameof(quotas));
        var result = await RetryAsync(async _ =>
        {
            using var uow = claims.BeginUnitOfWork();
            var session = uow.Session;
            var id = ItemDocumentId(itemId);
            var item = await session.LoadAsync<InventoryItem>(id, cancellationToken);
            if (item is null)
            {
                item = new InventoryItem { Id = id, ItemId = itemId };
                await session.StoreAsync(item, cancellationToken);
            }
            item.Quotas = quotas
                .GroupBy(q => q.QuotaId)
                .Select(g => new ItemQuotaMapping { QuotaId = g.Key, Multiplier = g.Sum(q => q.Multiplier) })
                .ToList();
            return await CommitAsync(uow, ClaimResult.Ok, [], cancellationToken);
        }, cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Item '{itemId}' could not be mapped: {result.Status}.");
    }

    public Task<ClaimResult> DefineChainAsync(string chainId, IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(chainId);
        var units = rows.SelectMany(r => r).ToList();
        if (units.Count != units.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("A unit appears twice in the chain.", nameof(rows));
        return RetryAsync(async _ =>
        {
            using var uow = claims.BeginUnitOfWork();
            var session = uow.Session;
            var chain = await session.LoadAsync<InventoryChain>(chainId, cancellationToken);
            if (chain is null)
            {
                chain = new InventoryChain { Id = chainId };
                await session.StoreAsync(chain, cancellationToken);
            }
            else if (chain.Cursor > 0 || chain.Holes.Count > 0)
            {
                return new ClaimResult(ClaimStatus.InvalidState, chainId);
            }
            chain.Rows = rows.Select(r => r.ToList()).ToList();
            return await CommitAsync(uow, ClaimResult.Ok, [], cancellationToken);
        }, cancellationToken);
    }

    // ---- claims --------------------------------------------------------------------------------------------

    public Task<ClaimResult> TryClaimAsync(ClaimRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        return RetryAsync(attempt => ClaimOnceAsync(request, attempt, cancellationToken), cancellationToken);
    }

    public Task<ClaimResult> AdjustAsync(ClaimRequest target, CancellationToken cancellationToken = default)
    {
        Validate(target);
        return RetryAsync(attempt => AdjustOnceAsync(target, attempt, cancellationToken), cancellationToken);
    }

    public Task<ClaimResult> ExtendAsync(string claimId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        => RetryAsync(_ => ExtendOnceAsync(claimId, expiresAt.ToUniversalTime(), cancellationToken), cancellationToken);

    public Task<ClaimResult> ConvertAsync(string claimId, IReadOnlyList<LimitLine>? lateLimits = null, CancellationToken cancellationToken = default)
        => RetryAsync(_ => ConvertOnceAsync(claimId, lateLimits ?? [], cancellationToken), cancellationToken);

    public Task<ClaimResult> ReleaseAsync(string claimId, ReturnReason reason, bool returnStock = true, CancellationToken cancellationToken = default)
        => RetryAsync(_ => ReleaseOnceAsync(claimId, reason, returnStock, cancellationToken), cancellationToken);

    /// <summary>A release as a cancellation (the spike tests' shorthand).</summary>
    public Task<ClaimResult> ReleaseAsync(string claimId, bool returnStock = true, CancellationToken cancellationToken = default)
        => ReleaseAsync(claimId, ReturnReason.Cancelled, returnStock, cancellationToken);

    public Task<ClaimResult> ReturnAsync(string claimId, InventoryReturn request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.ReturnId);
        return RetryAsync(_ => ReturnOnceAsync(claimId, request, cancellationToken), cancellationToken);
    }

    public Task<ClaimResult> RestockAsync(string claimId, string returnId, CancellationToken cancellationToken = default)
        => RetryAsync(_ => RestockOnceAsync(claimId, returnId, cancellationToken), cancellationToken);

    /// <summary>Prunes the quota's lapsed holds. Only display and housekeeping depend on it, never correctness (I3).</summary>
    public Task<ClaimResult> SweepAsync(string quotaId, CancellationToken cancellationToken = default)
        => RetryAsync(_ => PruneOnceAsync(quotaId, cancellationToken), cancellationToken);

    /// <summary>
    /// Releases up to <paramref name="max"/> held claims that lapsed (<c>ExpiresAt + grace</c> passed), as
    /// <see cref="ReturnReason.Expired"/>, and returns them. Housekeeping only (I3): it frees chain positions and limit
    /// holds early and lets checkout cancel the provider payment; the counts were already free.
    /// </summary>
    public async Task<IReadOnlyList<InventoryClaim>> ReleaseLapsedAsync(int max, CancellationToken cancellationToken = default)
    {
        var cutoff = Now - options.Grace;
        List<string> ids;
        using (var session = OpenReadSession())
        {
            // An auto-index over State and ExpiresAt; a stale result only delays the release (I3).
            ids = await session.Query<InventoryClaim>()
                .Where(c => c.State == ClaimState.Held && c.ExpiresAt < cutoff)
                .Take(max)
                .Select(c => c.Id!)
                .ToListAsync(cancellationToken);
        }

        var released = new List<InventoryClaim>();
        foreach (var id in ids)
        {
            var result = await ReleaseAsync(id, ReturnReason.Expired, cancellationToken: cancellationToken);
            if (result.Succeeded && await GetClaimAsync(id, cancellationToken) is { State: ClaimState.Released, ReleaseReason: ReturnReason.Expired } claim)
                released.Add(claim);
        }
        return released;
    }

    // ---- reads ---------------------------------------------------------------------------------------------

    public async Task<QuotaAvailability> GetAvailabilityAsync(string quotaId, CancellationToken cancellationToken = default)
    {
        using var session = OpenReadSession();
        var quota = await session.LoadAsync<InventoryQuota>(quotaId, cancellationToken)
            ?? throw new InvalidOperationException($"Quota '{quotaId}' does not exist.");
        var shards = await session.LoadAsync<InventoryQuotaShard>(Enumerable.Range(0, quota.ShardCount).Select(k => ShardId(quotaId, k)), cancellationToken);
        int capacity = 0, sold = 0, held = 0;
        foreach (var shard in shards.Values.OfType<InventoryQuotaShard>())
        {
            capacity += shard.CapacityShare;
            sold += shard.Sold;
            held += shard.Holds.Where(h => IsLive(h.ExpiresAt)).Sum(h => h.N);
        }
        return new QuotaAvailability(quotaId, capacity, sold, held, Math.Max(0, capacity - sold - held));
    }

    public async Task<IReadOnlyDictionary<string, UnitState>> GetUnitStatesAsync(IEnumerable<string> unitIds, CancellationToken cancellationToken = default)
    {
        var ids = unitIds.Distinct(StringComparer.Ordinal).ToArray();
        var operations = database is null ? store.Operations : store.Operations.ForDatabase(database);
        var values = ids.Length == 0
            ? new Dictionary<string, CompareExchangeValue<SparkClaimHold>>()
            : await operations.SendAsync(new GetCompareExchangeValuesOperation<SparkClaimHold>(ids.Select(UnitKey).ToArray()), token: cancellationToken);
        var states = new Dictionary<string, UnitState>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var key = unitKind.Key(id);
            values.TryGetValue(key.Value, out var value);
            states[id] = !IsLive(key, value) ? UnitState.Free
                : value!.Value.ExpiresAtUtc is null ? UnitState.Sold
                : UnitState.Held;
        }
        return states;
    }

    public async Task<InventoryClaim?> GetClaimAsync(string claimId, CancellationToken cancellationToken = default)
    {
        using var session = OpenReadSession();
        return await session.LoadAsync<InventoryClaim>(claimId, cancellationToken);
    }

    // ---- plumbing ------------------------------------------------------------------------------------------

    private async Task<ClaimResult> RetryAsync(Func<int, Task<ClaimResult>> operation, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            ClaimResult result;
            try
            {
                result = await operation(attempt);
            }
            catch (ConcurrencyException)
            {
                result = ConflictResult;
            }
            if (result.Status != ClaimStatus.Conflict || attempt + 1 >= options.MaxAttempts)
                return result;
            // Jittered backoff, growing with the attempt; the next attempt starts on another shard.
            await Task.Delay(Random.Shared.Next(1, 4 << Math.Min(attempt, 5)), cancellationToken);
        }
    }

    private async Task<ClaimResult> CommitAsync(ISparkClaimsUnitOfWork uow, ClaimResult ok, IEnumerable<string> touchedQuotas, CancellationToken cancellationToken)
    {
        if (!await uow.CommitAsync(cancellationToken))
            return ConflictResult;
        var touched = touchedQuotas.Distinct(StringComparer.Ordinal).ToList();
        if (committed is not null && touched.Count > 0)
            await committed(touched, cancellationToken);
        return ok;
    }

    private static void Validate(ClaimRequest request)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.ClaimId);
        if (request.Quotas.Any(l => l.N < 1) || request.Items?.Any(l => l.N < 1) == true
            || request.BestAvailable?.Any(l => l.N < 1) == true || request.Limits?.Any(l => l.N < 0 || l.Max < 0) == true)
            throw new ArgumentException("Every line takes at least one unit and every limit is non-negative.", nameof(request));
    }

    private static async Task<List<InventoryQuotaShard>> LoadShardsAsync(IAsyncDocumentSession session, InventoryQuota quota, CancellationToken cancellationToken)
    {
        var shards = await session.LoadAsync<InventoryQuotaShard>(Enumerable.Range(0, quota.ShardCount).Select(k => ShardId(quota.Id!, k)), cancellationToken);
        return shards.Values.OfType<InventoryQuotaShard>().ToList();
    }

    // ---- claim, adjust -------------------------------------------------------------------------------------

    private async Task<ClaimResult> ClaimOnceAsync(ClaimRequest request, int attempt, CancellationToken cancellationToken)
    {
        using var uow = claims.BeginUnitOfWork();
        var session = uow.Session;
        var existing = await session.LoadAsync<InventoryClaim>(request.ClaimId, cancellationToken);
        if (existing is not null)
            return existing.State == ClaimState.Held && IsLive(existing.ExpiresAt)
                ? ClaimResult.Ok with { Units = existing.Units.ToList() }
                : new ClaimResult(ClaimStatus.InvalidState, request.ClaimId);

        var expiresAt = request.ExpiresAt.ToUniversalTime();
        var claim = new InventoryClaim { Id = request.ClaimId, Owner = request.Owner, State = ClaimState.Held, ExpiresAt = expiresAt };
        var refused = await ApplyAsync(uow, request, claim, attempt, cancellationToken);
        if (refused is not null) return refused;

        await session.StoreAsync(claim, cancellationToken);
        return await CommitAsync(uow, ClaimResult.Ok with { Units = claim.Units.ToList() }, claim.Allocations.Select(a => a.QuotaId), cancellationToken);
    }

    private async Task<ClaimResult> AdjustOnceAsync(ClaimRequest target, int attempt, CancellationToken cancellationToken)
    {
        using var uow = claims.BeginUnitOfWork();
        var session = uow.Session;
        var claim = await session.LoadAsync<InventoryClaim>(target.ClaimId, cancellationToken);
        if (claim is null) return new ClaimResult(ClaimStatus.NotFound, target.ClaimId);
        if (claim.State != ClaimState.Held) return new ClaimResult(ClaimStatus.InvalidState, target.ClaimId);

        var expiresAt = target.ExpiresAt.ToUniversalTime();
        var touched = claim.Allocations.Select(a => a.QuotaId).ToList();

        // Quota units and limits: drop the claim's holds, then take the target's.
        foreach (var shardId in claim.Allocations.Select(a => a.ShardId).Distinct())
            (await session.LoadAsync<InventoryQuotaShard>(shardId, cancellationToken))?.Holds.RemoveAll(h => h.ClaimId == claim.Id);
        foreach (var limit in claim.Limits)
            (await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(LimitKey(limit.Key), cancellationToken))?.Value.Holds.RemoveAll(h => h.ClaimId == claim.Id);
        claim.Allocations.Clear();
        claim.Items.Clear();
        claim.Limits.Clear();

        // Explicit and picked seats: those the target no longer names go back; the rest are kept or taken below.
        var chainUnits = claim.Chains.SelectMany(c => c.Units).ToHashSet(StringComparer.Ordinal);
        var targetExplicit = ExplicitUnits(target).ToHashSet(StringComparer.Ordinal);
        await ReleaseUnitsAsync(uow, claim, claim.Units.Where(u => !chainUnits.Contains(u) && !targetExplicit.Contains(u)).ToList(), cancellationToken);
        claim.Units.RemoveAll(u => !chainUnits.Contains(u));

        // Chain lines: a shrinking line gives back its last seats, a growing one seats the extra as their own group.
        var wanted = (target.BestAvailable ?? []).GroupBy(l => l.ChainId).ToDictionary(g => g.Key, g => g.Sum(l => l.N), StringComparer.Ordinal);
        var extra = new List<ChainLine>();
        foreach (var allocation in claim.Chains.ToList())
        {
            var want = wanted.GetValueOrDefault(allocation.ChainId);
            wanted.Remove(allocation.ChainId);
            var have = allocation.Units.Count;
            if (want < have)
                await ReleaseUnitsAsync(uow, claim, allocation.Units.Skip(want).ToList(), cancellationToken);
            else if (want > have)
                extra.Add(new ChainLine(allocation.ChainId, want - have));
        }
        extra.AddRange(wanted.Where(w => w.Value > 0).Select(w => new ChainLine(w.Key, w.Value)));

        // The chain seats kept move to the new deadline; one taken over after expiry fails the re-claim.
        foreach (var unitId in claim.Chains.SelectMany(c => c.Units).ToList())
        {
            var update = await uow.UpdateAsync(unitKind, unitKind.Key(unitId), claim.Id!, hold => hold with { ExpiresAtUtc = expiresAt.UtcDateTime }, cancellationToken);
            if (!update.Holds) return new ClaimResult(ClaimStatus.Expired, unitId);
        }

        claim.ExpiresAt = expiresAt;
        var refused = await ApplyAsync(uow, target with { BestAvailable = extra }, claim, attempt, cancellationToken);
        if (refused is not null) return refused;

        touched.AddRange(claim.Allocations.Select(a => a.QuotaId));
        return await CommitAsync(uow, ClaimResult.Ok with { Units = claim.Units.ToList() }, touched, cancellationToken);
    }

    private static IEnumerable<string> ExplicitUnits(ClaimRequest request)
        => (request.Units ?? []).Concat((request.Picks ?? []).SelectMany(p => p.Picked)).Distinct(StringComparer.Ordinal);

    /// <summary>Takes everything <paramref name="request"/> names for <paramref name="claim"/>. Returns the refusal, or null on success.</summary>
    private async Task<ClaimResult?> ApplyAsync(ISparkClaimsUnitOfWork uow, ClaimRequest request, InventoryClaim claim, int attempt, CancellationToken cancellationToken)
    {
        var session = uow.Session;
        var expiresAt = claim.ExpiresAt!.Value;

        // Items resolve through their mappings (D15): N × multiplier from each quota.
        var lines = request.Quotas.ToList();
        foreach (var item in (request.Items ?? []).GroupBy(i => i.ItemId).Select(g => new ItemLine(g.Key, g.Sum(i => i.N))))
        {
            var mapping = await session.LoadAsync<InventoryItem>(ItemDocumentId(item.ItemId), cancellationToken);
            if (mapping is null || mapping.Quotas.Count == 0) return new ClaimResult(ClaimStatus.NotFound, item.ItemId);
            lines.AddRange(mapping.Quotas.Select(m => new QuotaLine(m.QuotaId, item.N * m.Multiplier)));
            claim.Items.Add(new ClaimItem { ItemId = item.ItemId, N = item.N });
        }
        foreach (var line in lines.GroupBy(l => l.QuotaId).Select(g => new QuotaLine(g.Key, g.Sum(l => l.N))))
        {
            var refused = await AllocateAsync(session, line, claim, expiresAt, attempt, cancellationToken);
            if (refused is not null) return refused;
        }

        // Picked seats: the single-seat rule, judged against the row as it stands in this transaction.
        foreach (var pick in request.Picks ?? [])
        {
            var picked = pick.Picked.ToHashSet(StringComparer.Ordinal);
            if (picked.FirstOrDefault(u => !pick.RowUnits.Contains(u)) is { } stray)
                return new ClaimResult(ClaimStatus.InvalidState, stray);
            var takenByOthers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var unitId in pick.RowUnits)
            {
                var key = unitKind.Key(unitId);
                var value = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<SparkClaimHold>(key.Value, cancellationToken);
                if (IsLive(key, value) && value!.Value.Owner != claim.Id)
                    takenByOthers.Add(unitId);
            }
            if (SeatGapRules.FindNewSingle(pick.RowUnits, takenByOthers.Contains, picked, pick.Rule ?? options.SingleSeatRule) is { } single)
                return new ClaimResult(ClaimStatus.LeavesSingleSeat, single);
        }

        foreach (var unitId in ExplicitUnits(request))
        {
            if (!await ReserveUnitAsync(uow, unitId, claim.Id!, expiresAt, cancellationToken))
                return new ClaimResult(ClaimStatus.UnitTaken, unitId);
            if (!claim.Units.Contains(unitId)) claim.Units.Add(unitId);
        }

        foreach (var line in (request.BestAvailable ?? []).GroupBy(l => l.ChainId).Select(g => new ChainLine(g.Key, g.Sum(l => l.N))))
        {
            var refused = await AllocateChainAsync(uow, line, claim, expiresAt, cancellationToken);
            if (refused is not null) return refused;
        }

        foreach (var limit in (request.Limits ?? []).GroupBy(l => l.Key).Select(g => new LimitLine(g.Key, g.Min(l => l.Max), g.Sum(l => l.N))))
        {
            var key = LimitKey(limit.Key);
            var entry = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(key, cancellationToken);
            var count = entry?.Value ?? new BuyerCount();
            count.Holds.RemoveAll(h => !IsLive(h.ExpiresAt));
            if (count.Count + count.Holds.Sum(h => h.N) + limit.N > limit.Max)
                return new ClaimResult(ClaimStatus.LimitExceeded, limit.Key);
            count.Holds.Add(new Hold { ClaimId = claim.Id!, N = limit.N, ExpiresAt = expiresAt });
            if (entry is null)
                session.Advanced.ClusterTransaction.CreateCompareExchangeValue(key, count);
            claim.Limits.Add(new LimitAllocation { Key = limit.Key, N = limit.N });
        }
        return null;
    }

    /// <summary>
    /// Takes <paramref name="line"/>'s units from one shard, starting from one chosen by claim id and attempt; when no
    /// single shard has room, splits them over all shards. Returns null on success.
    /// </summary>
    private async Task<ClaimResult?> AllocateAsync(IAsyncDocumentSession session, QuotaLine line, InventoryClaim claim, DateTimeOffset expiresAt, int attempt, CancellationToken cancellationToken)
    {
        var quota = await session.LoadAsync<InventoryQuota>(line.QuotaId, cancellationToken);
        if (quota is null) return new ClaimResult(ClaimStatus.NotFound, line.QuotaId);

        var start = (int)((uint)(StringComparer.Ordinal.GetHashCode(claim.Id!) + attempt * 7919) % (uint)quota.ShardCount);
        var first = await session.LoadAsync<InventoryQuotaShard>(ShardId(line.QuotaId, start), cancellationToken);
        if (first is not null && Room(first) >= line.N)
        {
            Take(first, line.N, claim, expiresAt);
            return null;
        }
        if (quota.ShardCount == 1) return new ClaimResult(ClaimStatus.SoldOut, line.QuotaId);

        var shards = await session.LoadAsync<InventoryQuotaShard>(Enumerable.Range(0, quota.ShardCount).Select(k => ShardId(line.QuotaId, (start + k) % quota.ShardCount)), cancellationToken);
        var ordered = shards.Values.OfType<InventoryQuotaShard>().ToList();
        if (ordered.Sum(Room) < line.N) return new ClaimResult(ClaimStatus.SoldOut, line.QuotaId);

        var remaining = line.N;
        foreach (var shard in ordered)
        {
            var n = Math.Min(Room(shard), remaining);
            if (n <= 0) continue;
            Take(shard, n, claim, expiresAt);
            remaining -= n;
            if (remaining == 0) break;
        }
        return null;
    }

    private int Room(InventoryQuotaShard shard)
    {
        shard.Holds.RemoveAll(h => !IsLive(h.ExpiresAt));
        return shard.CapacityShare - shard.Sold - shard.Holds.Sum(h => h.N);
    }

    private static void Take(InventoryQuotaShard shard, int n, InventoryClaim claim, DateTimeOffset expiresAt)
    {
        shard.Holds.Add(new Hold { ClaimId = claim.Id!, N = n, ExpiresAt = expiresAt });
        claim.Allocations.Add(new ShardAllocation { QuotaId = shard.QuotaId, ShardId = shard.Id!, N = n });
    }

    /// <summary>Chain fill (D25): proposes a run, checks each seat's claim, takes the run. Returns null on success.</summary>
    private async Task<ClaimResult?> AllocateChainAsync(ISparkClaimsUnitOfWork uow, ChainLine line, InventoryClaim claim, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var session = uow.Session;
        var chain = await session.LoadAsync<InventoryChain>(line.ChainId, cancellationToken);
        if (chain is null) return new ClaimResult(ClaimStatus.NotFound, line.ChainId);
        var starts = ChainFill.RowStarts(chain);

        while (true)
        {
            if (ChainFill.Propose(chain, line.N, out var refusal) is not { } positions)
                return new ClaimResult(refusal, line.ChainId);

            // A seat may be taken outside chain fill (picked by hand in the same block): skip it and propose again.
            var blocked = false;
            foreach (var position in positions)
            {
                var key = unitKind.Key(ChainFill.UnitAt(chain, starts, position));
                var value = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<SparkClaimHold>(key.Value, cancellationToken);
                if (IsLive(key, value) && value!.Value.Owner != claim.Id)
                {
                    ChainFill.Block(chain, position);
                    blocked = true;
                }
            }
            if (blocked) continue;

            ChainFill.Take(chain, positions);
            var units = positions.Select(p => ChainFill.UnitAt(chain, starts, p)).ToList();
            foreach (var unitId in units)
                if (!await ReserveUnitAsync(uow, unitId, claim.Id!, expiresAt, cancellationToken))
                    return new ClaimResult(ClaimStatus.UnitTaken, unitId);

            var allocation = claim.Chains.FirstOrDefault(c => c.ChainId == line.ChainId);
            if (allocation is null)
                claim.Chains.Add(allocation = new ChainAllocation { ChainId = line.ChainId });
            allocation.Positions.AddRange(positions);
            allocation.Units.AddRange(units);
            claim.Units.AddRange(units);
            return null;
        }
    }

    /// <summary>Holds a seat for the claim until <paramref name="expiresAt"/>: a new hold, a takeover after expiry plus grace, or the claim's own hold moved to the new deadline.</summary>
    private async Task<bool> ReserveUnitAsync(ISparkClaimsUnitOfWork uow, string unitId, string claimId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var key = unitKind.Key(unitId);
        var reserved = await uow.ReserveHoldAsync(unitKind, key, claimId, expiresAt.UtcDateTime, cancellationToken: cancellationToken);
        if (reserved.Status == SparkClaimStatus.AlreadyYours)
            reserved = await uow.UpdateAsync(unitKind, key, claimId, hold => hold with { ExpiresAtUtc = expiresAt.UtcDateTime }, cancellationToken);
        return reserved.Holds;
    }

    /// <summary>
    /// Gives seats back: deletes each one the claim still holds (one taken over after expiry is no longer its own) and
    /// returns their chain positions as holes. Returns the seats actually given back.
    /// </summary>
    private async Task<List<string>> ReleaseUnitsAsync(ISparkClaimsUnitOfWork uow, InventoryClaim claim, IReadOnlyCollection<string> units, CancellationToken cancellationToken)
    {
        var released = new List<string>();
        foreach (var unitId in units)
            if ((await uow.ReleaseAsync(unitKind, unitKind.Key(unitId), claim.Id!, cancellationToken)).Status == SparkClaimStatus.Released)
                released.Add(unitId);

        var gone = units.ToHashSet(StringComparer.Ordinal);
        var returned = released.ToHashSet(StringComparer.Ordinal);
        foreach (var allocation in claim.Chains)
        {
            var positions = new List<int>();
            for (var i = allocation.Units.Count - 1; i >= 0; i--)
            {
                if (!gone.Contains(allocation.Units[i])) continue;
                if (returned.Contains(allocation.Units[i])) positions.Add(allocation.Positions[i]);
                allocation.Units.RemoveAt(i);
                allocation.Positions.RemoveAt(i);
            }
            if (positions.Count > 0 && await uow.Session.LoadAsync<InventoryChain>(allocation.ChainId, cancellationToken) is { } chain)
                ChainFill.Return(chain, positions);
        }
        claim.Chains.RemoveAll(c => c.Units.Count == 0);
        claim.Units.RemoveAll(gone.Contains);
        return released;
    }

    // ---- extend, convert -----------------------------------------------------------------------------------

    private async Task<ClaimResult> ExtendOnceAsync(string claimId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        using var uow = claims.BeginUnitOfWork();
        var claim = await uow.Session.LoadAsync<InventoryClaim>(claimId, cancellationToken);
        if (claim is null) return new ClaimResult(ClaimStatus.NotFound, claimId);
        if (claim.State != ClaimState.Held) return new ClaimResult(ClaimStatus.InvalidState, claimId);
        if (!IsLive(claim.ExpiresAt)) return new ClaimResult(ClaimStatus.Expired, claimId);

        var refused = await VisitHoldsAsync(uow, claim, (hold, _) => hold.ExpiresAt = expiresAt, hold => hold with { ExpiresAtUtc = expiresAt.UtcDateTime }, cancellationToken);
        if (refused is not null) return refused;
        claim.ExpiresAt = expiresAt;
        return await CommitAsync(uow, ClaimResult.Ok, [], cancellationToken);
    }

    private async Task<ClaimResult> ConvertOnceAsync(string claimId, IReadOnlyList<LimitLine> lateLimits, CancellationToken cancellationToken)
    {
        using var uow = claims.BeginUnitOfWork();
        var session = uow.Session;
        var claim = await session.LoadAsync<InventoryClaim>(claimId, cancellationToken);
        if (claim is null) return new ClaimResult(ClaimStatus.NotFound, claimId);
        if (claim.State == ClaimState.Converted) return ClaimResult.Ok;
        if (claim.State != ClaimState.Held) return new ClaimResult(ClaimStatus.InvalidState, claimId);
        if (!IsLive(claim.ExpiresAt)) return new ClaimResult(ClaimStatus.Expired, claimId);

        var refused = await VisitHoldsAsync(uow, claim,
            (hold, owner) => owner.Remove(hold),
            hold => hold with { ExpiresAtUtc = null },
            cancellationToken,
            onShard: (shard, n) => shard.Sold += n,
            onLimit: (count, n) => count.Count += n);
        if (refused is not null) return refused;

        // Limit keys known only at payment (the card fingerprint, D29) count at once, or the conversion is refused.
        foreach (var limit in lateLimits.GroupBy(l => l.Key).Select(g => new LimitLine(g.Key, g.Min(l => l.Max), g.Sum(l => l.N))))
        {
            var key = LimitKey(limit.Key);
            var entry = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(key, cancellationToken);
            var count = entry?.Value ?? new BuyerCount();
            count.Holds.RemoveAll(h => !IsLive(h.ExpiresAt));
            if (count.Count + count.Holds.Sum(h => h.N) + limit.N > limit.Max)
                return new ClaimResult(ClaimStatus.LimitExceeded, limit.Key);
            count.Count += limit.N;
            if (entry is null)
                session.Advanced.ClusterTransaction.CreateCompareExchangeValue(key, count);
            var existing = claim.Limits.FirstOrDefault(l => l.Key == limit.Key);
            if (existing is null) claim.Limits.Add(new LimitAllocation { Key = limit.Key, N = limit.N });
            else existing.N += limit.N;
        }

        claim.State = ClaimState.Converted;
        claim.ExpiresAt = null;
        return await CommitAsync(uow, ClaimResult.Ok with { Units = claim.Units.ToList() }, [], cancellationToken);
    }

    /// <summary>
    /// Applies <paramref name="onHold"/> to each of the claim's quota and limit holds and <paramref name="onUnit"/> to
    /// each seat; fails with <see cref="ClaimStatus.Expired"/> when any of them is no longer the claim's.
    /// </summary>
    private async Task<ClaimResult?> VisitHoldsAsync(
        ISparkClaimsUnitOfWork uow,
        InventoryClaim claim,
        Action<Hold, List<Hold>> onHold,
        Func<SparkClaimHold, SparkClaimHold> onUnit,
        CancellationToken cancellationToken,
        Action<InventoryQuotaShard, int>? onShard = null,
        Action<BuyerCount, int>? onLimit = null)
    {
        var session = uow.Session;
        foreach (var group in claim.Allocations.GroupBy(a => a.ShardId))
        {
            var shard = await session.LoadAsync<InventoryQuotaShard>(group.Key, cancellationToken);
            var hold = shard?.Holds.FirstOrDefault(h => h.ClaimId == claim.Id);
            if (shard is null || hold is null) return new ClaimResult(ClaimStatus.Expired, claim.Id);
            onShard?.Invoke(shard, hold.N);
            onHold(hold, shard.Holds);
        }
        foreach (var unitId in claim.Units)
        {
            var updated = await uow.UpdateAsync(unitKind, unitKind.Key(unitId), claim.Id!, onUnit, cancellationToken);
            if (!updated.Holds) return new ClaimResult(ClaimStatus.Expired, unitId);
        }
        foreach (var limit in claim.Limits)
        {
            var entry = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(LimitKey(limit.Key), cancellationToken);
            var hold = entry?.Value.Holds.FirstOrDefault(h => h.ClaimId == claim.Id);
            if (entry is null || hold is null) return new ClaimResult(ClaimStatus.Expired, limit.Key);
            onLimit?.Invoke(entry.Value, hold.N);
            onHold(hold, entry.Value.Holds);
        }
        return null;
    }

    // ---- release, return, restock --------------------------------------------------------------------------

    private async Task<ClaimResult> ReleaseOnceAsync(string claimId, ReturnReason reason, bool returnStock, CancellationToken cancellationToken)
    {
        using var uow = claims.BeginUnitOfWork();
        var session = uow.Session;
        var claim = await session.LoadAsync<InventoryClaim>(claimId, cancellationToken);
        if (claim is null) return new ClaimResult(ClaimStatus.NotFound, claimId);
        if (claim.State == ClaimState.Released) return ClaimResult.Ok;

        var touched = claim.Allocations.Select(a => a.QuotaId).ToList();
        if (claim.State == ClaimState.Held)
        {
            // A lapsed hold may already be gone (pruned or taken over); whatever is still ours is dropped.
            foreach (var shardId in claim.Allocations.Select(a => a.ShardId).Distinct())
                (await session.LoadAsync<InventoryQuotaShard>(shardId, cancellationToken))?.Holds.RemoveAll(h => h.ClaimId == claimId);
            await ReleaseUnitsAsync(uow, claim, claim.Units.ToList(), cancellationToken);
            foreach (var limit in claim.Limits)
                (await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(LimitKey(limit.Key), cancellationToken))?.Value.Holds.RemoveAll(h => h.ClaimId == claimId);
        }
        else
        {
            // Everything not returned yet, as one return keyed by the release itself.
            if (claim.Returns.All(r => r.Id != ReleaseReturnId))
            {
                var everything = new InventoryReturn(ReleaseReturnId, reason, ReturnStock: returnStock);
                var refused = await ApplyReturnAsync(uow, claim, everything, cancellationToken);
                if (refused is not null) return refused;
            }
            if (!returnStock) touched.Clear();
        }

        claim.State = ClaimState.Released;
        claim.ReleaseReason = reason;
        claim.ExpiresAt = null;
        return await CommitAsync(uow, ClaimResult.Ok, touched, cancellationToken);
    }

    /// <summary>The return id a whole-claim release records on a converted claim.</summary>
    public const string ReleaseReturnId = "release";

    private async Task<ClaimResult> ReturnOnceAsync(string claimId, InventoryReturn request, CancellationToken cancellationToken)
    {
        using var uow = claims.BeginUnitOfWork();
        var claim = await uow.Session.LoadAsync<InventoryClaim>(claimId, cancellationToken);
        if (claim is null) return new ClaimResult(ClaimStatus.NotFound, claimId);
        if (claim.Returns.Any(r => r.Id == request.ReturnId)) return ClaimResult.Ok;
        // A held claim changes through AdjustAsync or ReleaseAsync; a released one has nothing left.
        if (claim.State != ClaimState.Converted) return new ClaimResult(ClaimStatus.InvalidState, claimId);

        var refused = await ApplyReturnAsync(uow, claim, request, cancellationToken);
        if (refused is not null) return refused;

        if (Remaining(claim).Shards.Values.All(n => n == 0) && RemainingUnits(claim).Count == 0)
        {
            claim.State = ClaimState.Released;
            claim.ReleaseReason = request.Reason;
        }
        var touched = request.ReturnStock ? claim.Returns[^1].Allocations.Select(a => a.QuotaId) : [];
        return await CommitAsync(uow, ClaimResult.Ok, touched, cancellationToken);
    }

    private async Task<ClaimResult?> ApplyReturnAsync(ISparkClaimsUnitOfWork uow, InventoryClaim claim, InventoryReturn request, CancellationToken cancellationToken)
    {
        var session = uow.Session;
        var remaining = Remaining(claim);
        var record = new ClaimReturn { Id = request.ReturnId, Reason = request.Reason, At = Now, StockReturned = request.ReturnStock };

        // Quota units.
        if (request.IsEverything)
        {
            foreach (var (shardId, n) in remaining.Shards.Where(s => s.Value > 0))
                record.Allocations.Add(new ShardAllocation { QuotaId = remaining.QuotaOf[shardId], ShardId = shardId, N = n });
        }
        else
        {
            var lines = (request.Quotas ?? []).ToList();
            foreach (var item in request.Items ?? [])
            {
                var mapping = await session.LoadAsync<InventoryItem>(ItemDocumentId(item.ItemId), cancellationToken);
                if (mapping is null) return new ClaimResult(ClaimStatus.NotFound, item.ItemId);
                lines.AddRange(mapping.Quotas.Select(m => new QuotaLine(m.QuotaId, item.N * m.Multiplier)));
            }
            foreach (var line in lines.GroupBy(l => l.QuotaId).Select(g => new QuotaLine(g.Key, g.Sum(l => l.N))))
            {
                var left = line.N;
                foreach (var (shardId, n) in remaining.Shards.Where(s => remaining.QuotaOf[s.Key] == line.QuotaId && s.Value > 0).ToList())
                {
                    var take = Math.Min(n, left);
                    record.Allocations.Add(new ShardAllocation { QuotaId = line.QuotaId, ShardId = shardId, N = take });
                    remaining.Shards[shardId] -= take;
                    left -= take;
                    if (left == 0) break;
                }
                if (left > 0) return new ClaimResult(ClaimStatus.InvalidState, line.QuotaId);
            }
        }

        // Seats always come back: they are never shipped.
        var remainingUnits = RemainingUnits(claim);
        var units = request.IsEverything ? remainingUnits.ToList() : (request.Units ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (units.FirstOrDefault(u => !remainingUnits.Contains(u)) is { } stray)
            return new ClaimResult(ClaimStatus.InvalidState, stray);

        // Per-buyer counts: named ones, or all of them on a full return (D16: returned on refund in both cases).
        var remainingLimits = claim.Limits.ToDictionary(l => l.Key, l => l.N - claim.Returns.SelectMany(r => r.Limits).Where(r => r.Key == l.Key).Sum(r => r.N), StringComparer.Ordinal);
        var limits = request.Limits?.Select(l => new LimitAllocation { Key = l.Key, N = l.N }).ToList()
            ?? (request.IsEverything ? remainingLimits.Where(l => l.Value > 0).Select(l => new LimitAllocation { Key = l.Key, N = l.Value }).ToList() : []);
        foreach (var limit in limits)
        {
            if (!remainingLimits.TryGetValue(limit.Key, out var left) || limit.N > left)
                return new ClaimResult(ClaimStatus.InvalidState, limit.Key);
        }

        if (request.ReturnStock)
            foreach (var allocation in record.Allocations)
            {
                var shard = await session.LoadAsync<InventoryQuotaShard>(allocation.ShardId, cancellationToken)
                    ?? throw new InvalidOperationException($"Shard '{allocation.ShardId}' of claim '{claim.Id}' is missing.");
                shard.Sold -= allocation.N;
            }

        // The claim's unit list keeps sold seats until they come back; a returned seat leaves it and its chain position.
        await ReleaseUnitsAsync(uow, claim, units, cancellationToken);
        record.Units.AddRange(units);

        foreach (var limit in limits.Where(l => l.N > 0))
        {
            var entry = await session.Advanced.ClusterTransaction.GetCompareExchangeValueAsync<BuyerCount>(LimitKey(limit.Key), cancellationToken);
            if (entry is not null) entry.Value.Count = Math.Max(0, entry.Value.Count - limit.N);
            record.Limits.Add(limit);
        }

        claim.Returns.Add(record);
        return null;
    }

    private async Task<ClaimResult> RestockOnceAsync(string claimId, string returnId, CancellationToken cancellationToken)
    {
        using var uow = claims.BeginUnitOfWork();
        var session = uow.Session;
        var claim = await session.LoadAsync<InventoryClaim>(claimId, cancellationToken);
        if (claim is null) return new ClaimResult(ClaimStatus.NotFound, claimId);
        var record = claim.Returns.FirstOrDefault(r => r.Id == returnId);
        if (record is null) return new ClaimResult(ClaimStatus.NotFound, returnId);
        if (record.StockReturned) return ClaimResult.Ok;

        foreach (var allocation in record.Allocations)
        {
            var shard = await session.LoadAsync<InventoryQuotaShard>(allocation.ShardId, cancellationToken)
                ?? throw new InvalidOperationException($"Shard '{allocation.ShardId}' of claim '{claimId}' is missing.");
            shard.Sold -= allocation.N;
        }
        record.StockReturned = true;
        return await CommitAsync(uow, ClaimResult.Ok, record.Allocations.Select(a => a.QuotaId), cancellationToken);
    }

    /// <summary>The quota units a converted claim still has sold, per shard.</summary>
    private static (Dictionary<string, int> Shards, Dictionary<string, string> QuotaOf) Remaining(InventoryClaim claim)
    {
        var shards = new Dictionary<string, int>(StringComparer.Ordinal);
        var quotaOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var allocation in claim.Allocations)
        {
            shards[allocation.ShardId] = shards.GetValueOrDefault(allocation.ShardId) + allocation.N;
            quotaOf[allocation.ShardId] = allocation.QuotaId;
        }
        foreach (var allocation in claim.Returns.SelectMany(r => r.Allocations))
            shards[allocation.ShardId] = shards.GetValueOrDefault(allocation.ShardId) - allocation.N;
        return (shards, quotaOf);
    }

    /// <summary>The seats a converted claim still has: its unit list (a returned seat leaves it).</summary>
    private static HashSet<string> RemainingUnits(InventoryClaim claim) => claim.Units.ToHashSet(StringComparer.Ordinal);

    // ---- housekeeping --------------------------------------------------------------------------------------

    private async Task<ClaimResult> PruneOnceAsync(string quotaId, CancellationToken cancellationToken)
    {
        using var uow = claims.BeginUnitOfWork();
        var quota = await uow.Session.LoadAsync<InventoryQuota>(quotaId, cancellationToken);
        if (quota is null) return new ClaimResult(ClaimStatus.NotFound, quotaId);
        var pruned = 0;
        foreach (var shard in await LoadShardsAsync(uow.Session, quota, cancellationToken))
            pruned += shard.Holds.RemoveAll(h => !IsLive(h.ExpiresAt));
        return pruned > 0 ? await CommitAsync(uow, ClaimResult.Ok, [quotaId], cancellationToken) : ClaimResult.Ok;
    }
}
