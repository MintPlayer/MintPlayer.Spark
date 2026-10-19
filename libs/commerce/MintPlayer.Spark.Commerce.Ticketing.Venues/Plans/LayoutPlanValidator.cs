using System.Text.RegularExpressions;
using MintPlayer.Spark.Commerce.Ticketing.Venues.Models;

namespace MintPlayer.Spark.Commerce.Ticketing.Venues.Plans;

/// <summary>The keys of a plan: they become parts of inventory ids (<c>{snapshotId}/{seatKey}</c>), so no slash and no space.</summary>
public static partial class LayoutKeys
{
    public const string Pattern = "^[A-Za-z0-9_.-]{1,64}$";

    [GeneratedRegex(Pattern)]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorRegex();

    public static bool IsValid(string? key) => key is not null && KeyRegex().IsMatch(key);

    public static bool IsColor(string? color) => color is not null && ColorRegex().IsMatch(color);
}

/// <summary>The bounds a plan is checked against (<c>Spark:Commerce:Ticketing:Venues</c>).</summary>
public sealed record LayoutPlanLimits(int MaxSeats = 100_000, int MaxBlocks = 2_000, int MaxPolygonPoints = 512, int MaxLabelLength = 32, int MaxNameLength = 120)
{
    public static readonly LayoutPlanLimits Default = new();
}

/// <summary>One problem of a plan: where (<c>blocks[3].rows[0].seats[12]</c>) and a translation key.</summary>
public sealed record LayoutProblem(string Path, string Code);

/// <summary>
/// Checks a plan before it is saved as a draft, submitted or published: keys valid and unique (seat keys across the
/// whole plan, since they become unit ids), geometry finite, references resolvable, each numbered block in at most one
/// chain (one chain per block group, §4.3), sizes within <see cref="LayoutPlanLimits"/>. Pure.
/// </summary>
public static class LayoutPlanValidator
{
    public static IReadOnlyList<LayoutProblem> Validate(LayoutPlan plan, LayoutPlanLimits? limits = null)
    {
        limits ??= LayoutPlanLimits.Default;
        var problems = new List<LayoutProblem>();
        void Add(string path, string code) { if (problems.Count < 200) problems.Add(new(path, code)); }

        if (!Finite(plan.Width) || !Finite(plan.Height) || plan.Width <= 0 || plan.Height <= 0 || plan.Width > 1e6 || plan.Height > 1e6)
            Add("size", "ticketingVenues.problem.size");
        if (plan.Background is { } bg && (!Finite(bg.X) || !Finite(bg.Y) || !Finite(bg.Width) || !Finite(bg.Height) || bg.Width <= 0 || bg.Height <= 0 || bg.Opacity is < 0 or > 1))
            Add("background", "ticketingVenues.problem.background");

        var categories = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < plan.Categories.Count; i++)
        {
            var category = plan.Categories[i];
            if (!LayoutKeys.IsValid(category.Key) || !categories.Add(category.Key))
                Add($"categories[{i}]", "ticketingVenues.problem.key");
            if (!LayoutKeys.IsColor(category.Color))
                Add($"categories[{i}]", "ticketingVenues.problem.color");
            if (string.IsNullOrWhiteSpace(category.Name) || category.Name.Length > limits.MaxNameLength)
                Add($"categories[{i}]", "ticketingVenues.problem.name");
        }

        if (plan.Blocks.Count > limits.MaxBlocks)
            Add("blocks", "ticketingVenues.problem.tooManyBlocks");

        var blocks = new Dictionary<string, LayoutBlock>(StringComparer.Ordinal);
        var seats = new Dictionary<string, (LayoutBlock Block, LayoutRow Row, LayoutSeat Seat)>(StringComparer.Ordinal);
        var seatCount = 0;
        for (var b = 0; b < plan.Blocks.Count; b++)
        {
            var block = plan.Blocks[b];
            var path = $"blocks[{b}]";
            if (!LayoutKeys.IsValid(block.Key) || !blocks.TryAdd(block.Key, block))
                Add(path, "ticketingVenues.problem.key");
            if (string.IsNullOrWhiteSpace(block.Name) || block.Name.Length > limits.MaxNameLength)
                Add(path, "ticketingVenues.problem.name");
            if (!IsPolygon(block.Polygon, limits.MaxPolygonPoints, minimumPoints: 3))
                Add(path, "ticketingVenues.problem.polygon");
            if (block.Category is { } blockCategory && !categories.Contains(blockCategory))
                Add(path, "ticketingVenues.problem.category");

            if (block.Kind == BlockKind.SeatedNumbered)
            {
                if (block.Rows.Count == 0)
                    Add(path, "ticketingVenues.problem.noRows");
            }
            else
            {
                if (block.Capacity <= 0 || block.Capacity > limits.MaxSeats)
                    Add(path, "ticketingVenues.problem.capacity");
                if (block.Rows.Count > 0)
                    Add(path, "ticketingVenues.problem.rowsInUnnumbered");
                continue;
            }

            var rowKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var r = 0; r < block.Rows.Count; r++)
            {
                var row = block.Rows[r];
                var rowPath = $"{path}.rows[{r}]";
                if (!LayoutKeys.IsValid(row.Key) || !rowKeys.Add(row.Key))
                    Add(rowPath, "ticketingVenues.problem.key");
                if (string.IsNullOrWhiteSpace(row.Label) || row.Label.Length > limits.MaxLabelLength)
                    Add(rowPath, "ticketingVenues.problem.label");
                if (row.Seats.Count == 0)
                    Add(rowPath, "ticketingVenues.problem.emptyRow");
                for (var s = 0; s < row.Seats.Count; s++)
                {
                    var seat = row.Seats[s];
                    var seatPath = $"{rowPath}.seats[{s}]";
                    seatCount++;
                    if (!LayoutKeys.IsValid(seat.Key) || !seats.TryAdd(seat.Key, (block, row, seat)))
                        Add(seatPath, "ticketingVenues.problem.key");
                    if (string.IsNullOrWhiteSpace(seat.Label) || seat.Label.Length > limits.MaxLabelLength)
                        Add(seatPath, "ticketingVenues.problem.label");
                    if (!Finite(seat.X) || !Finite(seat.Y))
                        Add(seatPath, "ticketingVenues.problem.position");
                    if ((seat.Tags & ~SeatTags.All) != 0)
                        Add(seatPath, "ticketingVenues.problem.tags");
                    if (seat.Category is { } seatCategory && !categories.Contains(seatCategory))
                        Add(seatPath, "ticketingVenues.problem.category");
                }
            }
        }
        if (seatCount > limits.MaxSeats)
            Add("blocks", "ticketingVenues.problem.tooManySeats");

        // A companion seat names an accessible seat of the same row (default 3: the wheelchair ticket holds it).
        foreach (var (key, (block, row, seat)) in seats)
        {
            if (seat.CompanionOf is not { Length: > 0 } of)
                continue;
            if ((seat.Tags & SeatTags.Companion) == 0 || !seats.TryGetValue(of, out var target) || target.Row != row || (target.Seat.Tags & SeatTags.Accessible) == 0)
                Add($"seat:{key}", "ticketingVenues.problem.companion");
        }

        var decorKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var d = 0; d < plan.Decor.Count; d++)
        {
            var decor = plan.Decor[d];
            if (!LayoutKeys.IsValid(decor.Key) || !decorKeys.Add(decor.Key))
                Add($"decor[{d}]", "ticketingVenues.problem.key");
            if (!IsPolygon(decor.Points, limits.MaxPolygonPoints, decor.Kind == DecorKind.Text ? 1 : 3))
                Add($"decor[{d}]", "ticketingVenues.problem.polygon");
            if (decor.Color is { Length: > 0 } color && !LayoutKeys.IsColor(color))
                Add($"decor[{d}]", "ticketingVenues.problem.color");
            if (decor.Label is { Length: > 0 } label && label.Length > limits.MaxNameLength)
                Add($"decor[{d}]", "ticketingVenues.problem.label");
        }

        var chainKeys = new HashSet<string>(StringComparer.Ordinal);
        var chained = new HashSet<string>(StringComparer.Ordinal);
        for (var c = 0; c < plan.Chains.Count; c++)
        {
            var chain = plan.Chains[c];
            if (!LayoutKeys.IsValid(chain.Key) || !chainKeys.Add(chain.Key))
                Add($"chains[{c}]", "ticketingVenues.problem.key");
            if (chain.Blocks.Count == 0)
                Add($"chains[{c}]", "ticketingVenues.problem.emptyChain");
            foreach (var entry in chain.Blocks)
            {
                if (!blocks.TryGetValue(entry.BlockKey, out var block) || block.Kind != BlockKind.SeatedNumbered)
                    Add($"chains[{c}]", "ticketingVenues.problem.chainBlock");
                else if (!chained.Add(entry.BlockKey))
                    Add($"chains[{c}]", "ticketingVenues.problem.chainedTwice");
            }
        }

        return problems;
    }

    private static bool Finite(double value) => double.IsFinite(value) && Math.Abs(value) <= 1e7;

    private static bool IsPolygon(List<double> points, int maxPoints, int minimumPoints)
        => points.Count % 2 == 0 && points.Count / 2 >= minimumPoints && points.Count / 2 <= maxPoints && points.All(Finite);
}
