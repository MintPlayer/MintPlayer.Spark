using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;

/// <summary>
/// How a snapshot maps onto the inventory engine (§4.3, D7, D25). Pure: it names ids and builds claim lines; the
/// engine does the claiming.
/// <list type="bullet">
/// <item>every numbered seat is the unit <c>{snapshotId}/{seatKey}</c>;</item>
/// <item>every chain is the inventory chain <c>{snapshotId}/chains/{chainKey}</c>, one inventory row per plan row in
/// the chain's direction, so a group never spans two rows;</item>
/// <item>every standing or unnumbered block is the quota <c>{snapshotId}/blocks/{blockKey}</c> with its capacity.</item>
/// </list>
/// Seats whose tags are in <see cref="ExcludedFromChains"/> (accessible and companion seats by default) are never
/// chain-filled: a buyer without the matching ticket type must not be seated there by best available (default 3).
/// </summary>
public sealed class SnapshotInventory
{
    private readonly Dictionary<string, (LayoutBlock Block, LayoutRow Row, int Index)> seats = new(StringComparer.Ordinal);

    public SnapshotInventory(string snapshotId, LayoutPlan plan, int excludedFromChains = SeatTags.Restricting)
    {
        SnapshotId = snapshotId;
        Plan = plan;
        ExcludedFromChains = excludedFromChains;
        foreach (var block in plan.Blocks.Where(b => b.Kind == BlockKind.SeatedNumbered))
            foreach (var row in block.Rows)
                for (var i = 0; i < row.Seats.Count; i++)
                    seats[row.Seats[i].Key] = (block, row, i);
    }

    public string SnapshotId { get; }

    public LayoutPlan Plan { get; }

    public int ExcludedFromChains { get; }

    public string UnitId(string seatKey) => $"{SnapshotId}/{seatKey}";

    public string ChainId(string chainKey) => $"{SnapshotId}/chains/{chainKey}";

    public string QuotaId(string blockKey) => $"{SnapshotId}/blocks/{blockKey}";

    /// <summary>The seat key of a unit of this snapshot, or null for a unit of another one.</summary>
    public string? SeatKeyOf(string unitId)
        => unitId.StartsWith(SnapshotId + "/", StringComparison.Ordinal) && unitId.Length > SnapshotId.Length + 1
            ? unitId[(SnapshotId.Length + 1)..]
            : null;

    public bool HasSeat(string seatKey) => seats.ContainsKey(seatKey);

    /// <summary>Every unit of the snapshot in seat-map order (block by block, row by row): the order of the live state frames.</summary>
    public IEnumerable<string> UnitsInMapOrder()
        => Plan.Blocks.Where(b => b.Kind == BlockKind.SeatedNumbered).SelectMany(b => b.Rows).SelectMany(r => r.Seats).Select(s => UnitId(s.Key));

    /// <summary>The rows of one chain, as <see cref="ISparkInventory.DefineChainAsync"/> takes them.</summary>
    public IReadOnlyList<IReadOnlyList<string>> ChainRows(LayoutChain chain)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var entry in chain.Blocks)
        {
            var block = Plan.Blocks.FirstOrDefault(b => b.Key == entry.BlockKey && b.Kind == BlockKind.SeatedNumbered);
            if (block is null)
                continue;
            IEnumerable<LayoutRow> blockRows = entry.BackToFront ? Enumerable.Reverse(block.Rows) : block.Rows;
            foreach (var row in blockRows)
            {
                IEnumerable<LayoutSeat> rowSeats = entry.ReverseSeats ? Enumerable.Reverse(row.Seats) : row.Seats;
                var units = rowSeats.Where(s => (s.Tags & ExcludedFromChains) == 0).Select(s => UnitId(s.Key)).ToList();
                if (units.Count > 0)
                    rows.Add(units);
            }
        }
        return rows;
    }

    /// <summary>The standing and unnumbered blocks with their capacities: the quotas to create.</summary>
    public IEnumerable<(string QuotaId, int Capacity)> BlockQuotas()
        => Plan.Blocks.Where(b => b.Kind != BlockKind.SeatedNumbered).Select(b => (QuotaId(b.Key), b.Capacity));

    /// <summary>
    /// Defines the snapshot's chains and block quotas in the engine. Idempotent: an existing quota is left alone, and a
    /// chain is redefined to the same rows (refused, harmlessly, once a seat of it was given out).
    /// </summary>
    public async Task ProvisionAsync(ISparkInventory inventory, int shardsPerQuota, CancellationToken cancellationToken)
    {
        foreach (var (quotaId, capacity) in BlockQuotas())
            await inventory.CreateQuotaAsync(quotaId, capacity, Math.Clamp(capacity / 250, 1, Math.Max(1, shardsPerQuota)), cancellationToken);
        foreach (var chain in Plan.Chains)
        {
            var rows = ChainRows(chain);
            if (rows.Count > 0)
                await inventory.DefineChainAsync(ChainId(chain.Key), rows, cancellationToken);
        }
    }

    /// <summary>
    /// The picks of a buyer who chose seats on the map (pick your seat, D7): one <see cref="SeatPick"/> per row, with
    /// the whole row so the engine can check the single-empty-seat rule. Unknown seat keys throw.
    /// </summary>
    public IReadOnlyList<SeatPick> Picks(IEnumerable<string> seatKeys, SingleSeatRule? rule = null)
    {
        var byRow = new Dictionary<LayoutRow, List<string>>();
        foreach (var key in seatKeys.Distinct(StringComparer.Ordinal))
        {
            if (!seats.TryGetValue(key, out var seat))
                throw new ArgumentException($"The seat '{key}' is not in snapshot '{SnapshotId}'.", nameof(seatKeys));
            if (!byRow.TryGetValue(seat.Row, out var picked))
                byRow[seat.Row] = picked = [];
            picked.Add(UnitId(key));
        }
        return byRow.Select(pair => new SeatPick(pair.Key.Seats.Select(s => UnitId(s.Key)).ToList(), pair.Value, rule)).ToList();
    }

    /// <summary>Best available along a chain (D25): <paramref name="n"/> seats together.</summary>
    public ChainLine BestAvailable(string chainKey, int n) => new(ChainId(chainKey), n);

    /// <summary><paramref name="n"/> places in a standing or unnumbered block.</summary>
    public QuotaLine Places(string blockKey, int n) => new(QuotaId(blockKey), n);
}
