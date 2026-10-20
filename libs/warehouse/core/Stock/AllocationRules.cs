namespace MintPlayer.Spark.Warehouse.Stock;

/// <summary>
/// The built-in allocation rules (PRD §4.7, WD4; <c>Spark:Warehouse:Allocation:Rule</c>). They only rank: the engine claims
/// in order under optimistic concurrency.
/// </summary>
internal static class AllocationOrder
{
    /// <summary>FEFO: earliest removal date (expiry minus minimum shelf life) first; ties to the lowest lot, then the earliest receipt. Lots inside the shelf life are left out.</summary>
    public static IReadOnlyList<AllocationCandidate> Fefo(AllocationRequest request, IEnumerable<AllocationCandidate> candidates)
        => candidates
            .Where(c => c.Expiry is null || c.Expiry.Value.AddDays(-request.MinimumShelfLifeDays) >= request.Today)
            .Where(c => request.RequiredLotCode is null || c.LotCode == request.RequiredLotCode)
            .OrderBy(c => c.Expiry is null ? 1 : 0)
            .ThenBy(c => c.Expiry?.AddDays(-request.MinimumShelfLifeDays))
            .ThenBy(c => c.LotCode, StringComparer.Ordinal)
            .ThenBy(c => c.ReceivedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(c => c.Location.WalkSequence)
            .ToList();

    /// <summary>FIFO: earliest receipt first, then walk sequence.</summary>
    public static IReadOnlyList<AllocationCandidate> Fifo(AllocationRequest request, IEnumerable<AllocationCandidate> candidates)
        => candidates
            .Where(c => request.RequiredLotCode is null || c.LotCode == request.RequiredLotCode)
            .OrderBy(c => c.ReceivedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(c => c.Location.WalkSequence)
            .ToList();

    /// <summary>Closest location: walk sequence only (fewest steps), the most available first on a tie.</summary>
    public static IReadOnlyList<AllocationCandidate> Closest(AllocationRequest request, IEnumerable<AllocationCandidate> candidates)
        => candidates
            .Where(c => request.RequiredLotCode is null || c.LotCode == request.RequiredLotCode)
            .OrderBy(c => c.Location.WalkSequence)
            .ThenByDescending(c => c.Available)
            .ToList();
}

internal sealed class FefoAllocationRule : IAllocationRule
{
    public string Name => WarehouseStrategyNames.Fefo;

    public ValueTask<IReadOnlyList<AllocationCandidate>> RankAsync(AllocationRequest request, IReadOnlyList<AllocationCandidate> candidates, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AllocationOrder.Fefo(request, candidates));
}

internal sealed class FifoAllocationRule : IAllocationRule
{
    public string Name => WarehouseStrategyNames.Fifo;

    public ValueTask<IReadOnlyList<AllocationCandidate>> RankAsync(AllocationRequest request, IReadOnlyList<AllocationCandidate> candidates, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AllocationOrder.Fifo(request, candidates));
}

internal sealed class ClosestLocationAllocationRule : IAllocationRule
{
    public string Name => WarehouseStrategyNames.ClosestLocation;

    public ValueTask<IReadOnlyList<AllocationCandidate>> RankAsync(AllocationRequest request, IReadOnlyList<AllocationCandidate> candidates, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(AllocationOrder.Closest(request, candidates));
}
