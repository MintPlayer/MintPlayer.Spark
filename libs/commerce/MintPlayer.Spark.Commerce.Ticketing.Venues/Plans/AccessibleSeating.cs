using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;

/// <summary>Why a seat may not be sold with a ticket type (default 3).</summary>
public enum SeatRefusal
{
    /// <summary>The seat is a wheelchair space and the ticket type does not allow <see cref="SeatTags.Accessible"/>.</summary>
    AccessibleOnly,

    /// <summary>The seat is a companion seat whose wheelchair space is not in the same selection, and the ticket type does not allow <see cref="SeatTags.Companion"/>.</summary>
    CompanionOnly,

    /// <summary>The seat is not in the plan.</summary>
    Unknown,
}

public sealed record SeatViolation(string SeatKey, SeatRefusal Refusal);

/// <summary>
/// Accessible seating (default 3, §4.11): seat tags; ticket types restricted to tagged seats; a wheelchair ticket can
/// hold the adjacent companion seat. Ticket types live in Ticketing.Events (M13); they state which restricting tags
/// they allow (<see cref="SeatTags.Accessible"/>, <see cref="SeatTags.Companion"/>), and checkout asks this class before
/// it claims. Pure.
/// </summary>
public static class AccessibleSeating
{
    /// <summary>
    /// The seats of <paramref name="selection"/> the ticket type may not take. <paramref name="allowedTags"/> are the
    /// restricting tags the ticket type allows. A companion seat is fine without the tag when its wheelchair space is
    /// in the same selection (the companion travels with the wheelchair ticket).
    /// </summary>
    public static IReadOnlyList<SeatViolation> Check(LayoutPlan plan, IReadOnlyCollection<string> selection, int allowedTags)
    {
        var index = Index(plan);
        var selected = selection.ToHashSet(StringComparer.Ordinal);
        var violations = new List<SeatViolation>();
        foreach (var key in selection)
        {
            if (!index.TryGetValue(key, out var found))
            {
                violations.Add(new(key, SeatRefusal.Unknown));
                continue;
            }
            var tags = found.Seat.Tags;
            if ((tags & SeatTags.Accessible) != 0 && (allowedTags & SeatTags.Accessible) == 0)
                violations.Add(new(key, SeatRefusal.AccessibleOnly));
            else if ((tags & SeatTags.Companion) != 0 && (allowedTags & SeatTags.Companion) == 0
                     && !(CompanionTarget(found.Row, found.Index) is { } target && selected.Contains(target)))
                violations.Add(new(key, SeatRefusal.CompanionOnly));
        }
        return violations;
    }

    /// <summary>
    /// The companion seats that belong to the wheelchair space <paramref name="accessibleSeatKey"/>: those naming it in
    /// <see cref="LayoutSeat.CompanionOf"/>, else the untargeted companion seats right beside it in its row.
    /// </summary>
    public static IReadOnlyList<string> CompanionsOf(LayoutPlan plan, string accessibleSeatKey)
    {
        if (!Index(plan).TryGetValue(accessibleSeatKey, out var found) || (found.Seat.Tags & SeatTags.Accessible) == 0)
            return [];
        var named = found.Row.Seats.Where(s => (s.Tags & SeatTags.Companion) != 0 && s.CompanionOf == accessibleSeatKey).Select(s => s.Key).ToList();
        if (named.Count > 0)
            return named;
        var adjacent = new List<string>();
        foreach (var i in new[] { found.Index - 1, found.Index + 1 })
            if (i >= 0 && i < found.Row.Seats.Count && found.Row.Seats[i] is { } s && (s.Tags & SeatTags.Companion) != 0 && string.IsNullOrEmpty(s.CompanionOf))
                adjacent.Add(s.Key);
        return adjacent;
    }

    /// <summary>The wheelchair space a companion seat belongs to: its <see cref="LayoutSeat.CompanionOf"/>, else an adjacent accessible seat.</summary>
    private static string? CompanionTarget(LayoutRow row, int index)
    {
        var seat = row.Seats[index];
        if (seat.CompanionOf is { Length: > 0 } of)
            return of;
        foreach (var i in new[] { index - 1, index + 1 })
            if (i >= 0 && i < row.Seats.Count && (row.Seats[i].Tags & SeatTags.Accessible) != 0)
                return row.Seats[i].Key;
        return null;
    }

    private static Dictionary<string, (LayoutRow Row, int Index, LayoutSeat Seat)> Index(LayoutPlan plan)
    {
        var index = new Dictionary<string, (LayoutRow, int, LayoutSeat)>(StringComparer.Ordinal);
        foreach (var block in plan.Blocks)
            foreach (var row in block.Rows)
                for (var i = 0; i < row.Seats.Count; i++)
                    index[row.Seats[i].Key] = (row, i, row.Seats[i]);
        return index;
    }
}
