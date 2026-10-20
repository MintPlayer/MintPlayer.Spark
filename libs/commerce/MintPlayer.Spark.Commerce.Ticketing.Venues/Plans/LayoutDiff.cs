using MintPlayer.Spark.Commerce.Ticketing.Venues.Contracts;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;

/// <summary>
/// The custom visual diff of a layout publication (§4.13: "seat layouts get a custom visual diff"): which seats a
/// proposal adds, removes, moves or retags against the published version, and which blocks and chains changed. The
/// approver's page draws the proposal with these seats highlighted. Pure. Seat lists are capped; the counts are not.
/// </summary>
public static class LayoutDiff
{
    public const int MaxListed = 5_000;

    public static LayoutDiffResult Compare(LayoutPlan? published, LayoutPlan proposal)
    {
        var before = Seats(published);
        var after = Seats(proposal);
        var added = after.Keys.Where(k => !before.ContainsKey(k)).ToList();
        var removed = before.Keys.Where(k => !after.ContainsKey(k)).ToList();
        var moved = new List<string>();
        var changed = new List<string>();
        foreach (var (key, seat) in after)
        {
            if (!before.TryGetValue(key, out var old))
                continue;
            if (Math.Abs(old.Seat.X - seat.Seat.X) > 0.01 || Math.Abs(old.Seat.Y - seat.Seat.Y) > 0.01)
                moved.Add(key);
            else if (old.Seat.Tags != seat.Seat.Tags || old.Seat.Category != seat.Seat.Category || old.Seat.Label != seat.Seat.Label
                     || old.Row != seat.Row || old.Block != seat.Block || old.Seat.CompanionOf != seat.Seat.CompanionOf)
                changed.Add(key);
        }

        var beforeBlocks = (published?.Blocks ?? []).ToDictionary(b => b.Key, StringComparer.Ordinal);
        var changedBlocks = proposal.Blocks
            .Where(b => !beforeBlocks.TryGetValue(b.Key, out var old) || old.Kind != b.Kind || old.Capacity != b.Capacity
                        || old.Category != b.Category || old.Name != b.Name || !old.Polygon.SequenceEqual(b.Polygon))
            .Select(b => b.Key)
            .Concat(beforeBlocks.Keys.Where(k => proposal.Blocks.All(b => b.Key != k)))
            .ToList();
        var beforeChains = (published?.Chains ?? []).ToDictionary(c => c.Key, Signature, StringComparer.Ordinal);
        var changedChains = proposal.Chains.Where(c => !beforeChains.TryGetValue(c.Key, out var sig) || sig != Signature(c)).Select(c => c.Key)
            .Concat(beforeChains.Keys.Where(k => proposal.Chains.All(c => c.Key != k)))
            .ToList();

        return new LayoutDiffResult(
            added.Count, removed.Count, moved.Count, changed.Count,
            added.Take(MaxListed).ToList(), removed.Take(MaxListed).ToList(), moved.Take(MaxListed).ToList(), changed.Take(MaxListed).ToList(),
            changedBlocks, changedChains);
    }

    private static string Signature(LayoutChain chain)
        => string.Join(';', chain.Blocks.Select(b => $"{b.BlockKey}:{b.BackToFront}:{b.ReverseSeats}"));

    private static Dictionary<string, (string Block, string Row, LayoutSeat Seat)> Seats(LayoutPlan? plan)
    {
        var seats = new Dictionary<string, (string, string, LayoutSeat)>(StringComparer.Ordinal);
        if (plan is null)
            return seats;
        foreach (var block in plan.Blocks)
            foreach (var row in block.Rows)
                foreach (var seat in row.Seats)
                    seats[seat.Key] = (block.Key, row.Key, seat);
        return seats;
    }
}
