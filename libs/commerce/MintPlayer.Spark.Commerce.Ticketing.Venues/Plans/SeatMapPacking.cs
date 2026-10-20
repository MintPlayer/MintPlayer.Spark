using MintPlayer.Spark.Commerce.Inventory;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;

/// <summary>
/// Packs a snapshot for the browser (S7): the plan as parallel arrays, and the seat states as 2 bits per seat, so a
/// 50k-seat map is one compact payload plus a ~17 KB state frame. Pure.
/// </summary>
public static class SeatMapPacking
{
    public static SeatMapPayload Payload(string snapshotId, string venueName, LayoutPlan plan)
    {
        var categoryIndex = plan.Categories.Select((c, i) => (c.Key, i)).ToDictionary(p => p.Key, p => p.i, StringComparer.Ordinal);
        int Category(string? key) => key is not null && categoryIndex.TryGetValue(key, out var i) ? i : -1;

        var keys = new List<string>();
        var labels = new List<string>();
        var xs = new List<double>();
        var ys = new List<double>();
        var rowOf = new List<int>();
        var tags = new List<int>();
        var categories = new List<int>();
        var rowLabels = new List<string>();
        var rowBlock = new List<int>();
        var rowStart = new List<int>();
        var rowCount = new List<int>();
        var blocks = new List<SeatMapBlock>();

        for (var b = 0; b < plan.Blocks.Count; b++)
        {
            var block = plan.Blocks[b];
            var seatStart = keys.Count;
            var firstRow = rowLabels.Count;
            if (block.Kind == BlockKind.SeatedNumbered)
            {
                foreach (var row in block.Rows)
                {
                    rowLabels.Add(row.Label);
                    rowBlock.Add(b);
                    rowStart.Add(keys.Count);
                    rowCount.Add(row.Seats.Count);
                    foreach (var seat in row.Seats)
                    {
                        keys.Add(seat.Key);
                        labels.Add(seat.Label);
                        xs.Add(Math.Round(seat.X, 2));
                        ys.Add(Math.Round(seat.Y, 2));
                        rowOf.Add(rowLabels.Count - 1);
                        tags.Add(seat.Tags);
                        categories.Add(Category(seat.Category ?? block.Category));
                    }
                }
            }
            blocks.Add(new SeatMapBlock(block.Key, block.Name, block.Kind, block.Polygon, block.Kind == BlockKind.SeatedNumbered ? keys.Count - seatStart : block.Capacity,
                Category(block.Category), seatStart, keys.Count - seatStart, firstRow, rowLabels.Count - firstRow));
        }

        return new SeatMapPayload(snapshotId, venueName, plan.Width, plan.Height, plan.Background, plan.Categories, blocks, plan.Decor,
            plan.Chains.Select(c => new SeatMapChain(c.Key, c.Name, c.Blocks.Select(b => b.BlockKey).ToList())).ToList(),
            new SeatMapRows(rowLabels, rowBlock, rowStart, rowCount),
            new SeatMapSeats(keys, labels, xs, ys, rowOf, tags, categories));
    }

    /// <summary>2 bits per seat in map order: 0 free, 1 held, 2 sold.</summary>
    public static byte[] PackStates(IReadOnlyList<UnitState> states)
    {
        var bytes = new byte[(states.Count + 3) / 4];
        for (var i = 0; i < states.Count; i++)
        {
            var value = states[i] switch { UnitState.Held => 1, UnitState.Sold => 2, _ => 0 };
            bytes[i >> 2] |= (byte)(value << ((i & 3) * 2));
        }
        return bytes;
    }

    public static UnitState UnpackState(byte[] packed, int index)
        => ((packed[index >> 2] >> ((index & 3) * 2)) & 3) switch { 1 => UnitState.Held, 2 => UnitState.Sold, _ => UnitState.Free };
}
