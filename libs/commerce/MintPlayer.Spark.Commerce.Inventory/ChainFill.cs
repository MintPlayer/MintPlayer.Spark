namespace MintPlayer.Spark.Commerce.Inventory;

/// <summary>
/// Chain fill (D25, §4.3): the pure part of best available. A group of <c>n</c> gets the smallest hole before the
/// cursor that fits it side by side in one row; otherwise the next run from the cursor, skipping a row's tail that is
/// too short (the skipped seats become holes for smaller groups). Positions are flat indexes over the chain's rows.
/// </summary>
/// <remarks>
/// The chain document is only the allocator's memory. The seats themselves are compare-exchange claims, so the
/// engine checks every proposed seat before it takes it and <see cref="Block"/>s one that turns out taken (a seat a
/// buyer picked by hand in the same block).
/// </remarks>
public static class ChainFill
{
    /// <summary>The flat position each row starts at, plus the total as the last entry.</summary>
    public static int[] RowStarts(InventoryChain chain)
    {
        var starts = new int[chain.Rows.Count + 1];
        for (var r = 0; r < chain.Rows.Count; r++)
            starts[r + 1] = starts[r] + chain.Rows[r].Count;
        return starts;
    }

    /// <summary>The unit at <paramref name="position"/>.</summary>
    public static string UnitAt(InventoryChain chain, int[] starts, int position)
    {
        var row = RowOf(starts, position);
        return chain.Rows[row][position - starts[row]];
    }

    /// <summary>How many seats of the chain are free (holes plus everything from the cursor).</summary>
    public static int Free(InventoryChain chain) => chain.Holes.Count + Math.Max(0, RowStarts(chain)[^1] - chain.Cursor);

    /// <summary>
    /// The positions a group of <paramref name="n"/> would get, or null with the reason in <paramref name="refusal"/>:
    /// <see cref="ClaimStatus.NotTogether"/> when enough seats are free but not side by side, else
    /// <see cref="ClaimStatus.SoldOut"/>. Changes nothing.
    /// </summary>
    public static int[]? Propose(InventoryChain chain, int n, out ClaimStatus refusal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        var starts = RowStarts(chain);
        refusal = ClaimStatus.Held;

        // 1. The smallest hole that fits, the earliest of equals.
        int bestStart = -1, bestLength = int.MaxValue;
        var holes = chain.Holes;
        for (var i = 0; i < holes.Count;)
        {
            var row = RowOf(starts, holes[i]);
            var j = i + 1;
            while (j < holes.Count && holes[j] == holes[j - 1] + 1 && holes[j] < starts[row + 1])
                j++;
            var length = j - i;
            if (length >= n && length < bestLength)
            {
                bestStart = holes[i];
                bestLength = length;
            }
            i = j;
        }
        if (bestStart >= 0)
            return Enumerable.Range(bestStart, n).ToArray();

        // 2. The next run from the cursor that fits in one row.
        var total = starts[^1];
        for (var c = chain.Cursor; c < total;)
        {
            var rowEnd = starts[RowOf(starts, c) + 1];
            if (rowEnd - c >= n)
                return Enumerable.Range(c, n).ToArray();
            c = rowEnd;
        }

        refusal = Free(chain) >= n ? ClaimStatus.NotTogether : ClaimStatus.SoldOut;
        return null;
    }

    /// <summary>Takes <paramref name="positions"/> (a proposal): out of the holes, or from the cursor run.</summary>
    public static void Take(InventoryChain chain, IReadOnlyList<int> positions)
    {
        if (positions.Count == 0)
            return;
        if (positions[0] < chain.Cursor)
        {
            var taken = positions.ToHashSet();
            chain.Holes.RemoveAll(taken.Contains);
            return;
        }
        // The row tail skipped to reach the run stays free for smaller groups.
        for (var p = chain.Cursor; p < positions[0]; p++)
            chain.Holes.Add(p);
        chain.Cursor = positions[^1] + 1;
        chain.Holes.Sort();
    }

    /// <summary>Marks <paramref name="position"/> as taken by someone else: chain fill no longer offers it.</summary>
    public static void Block(InventoryChain chain, int position)
    {
        if (position < chain.Cursor)
        {
            chain.Holes.Remove(position);
            return;
        }
        for (var p = chain.Cursor; p < position; p++)
            chain.Holes.Add(p);
        chain.Cursor = position + 1;
        chain.Holes.Sort();
    }

    /// <summary>Gives <paramref name="positions"/> back. A hole right before the cursor rewinds it.</summary>
    public static void Return(InventoryChain chain, IEnumerable<int> positions)
    {
        var holes = chain.Holes.ToHashSet();
        foreach (var p in positions)
            if (p < chain.Cursor && holes.Add(p))
                chain.Holes.Add(p);
        chain.Holes.Sort();
        while (chain.Holes.Count > 0 && chain.Holes[^1] == chain.Cursor - 1)
        {
            chain.Holes.RemoveAt(chain.Holes.Count - 1);
            chain.Cursor--;
        }
    }

    private static int RowOf(int[] starts, int position)
    {
        // starts is ascending; the row is the last start at or before the position (empty rows are skipped).
        var index = Array.BinarySearch(starts, 0, starts.Length - 1, position);
        if (index < 0)
            return ~index - 1;
        while (index + 1 < starts.Length - 1 && starts[index + 1] == position)
            index++;
        return index;
    }
}

/// <summary>The single-empty-seat rule of pick your seat (§4.3), as a pure check over one row.</summary>
public static class SeatGapRules
{
    /// <summary>
    /// The first seat the pick would leave single that was not single before, or null when the pick is fine. Only a
    /// newly created single counts, so a row that already has one (or has one seat left) can still be sold.
    /// </summary>
    /// <param name="row">The row's units in seat order.</param>
    /// <param name="takenByOthers">Whether a unit is held or sold by another claim.</param>
    /// <param name="picked">The units being picked in this row.</param>
    /// <param name="rule">Which gaps count.</param>
    public static string? FindNewSingle(IReadOnlyList<string> row, Func<string, bool> takenByOthers, IReadOnlySet<string> picked, SingleSeatRule rule)
    {
        if (rule == SingleSeatRule.Off || row.Count == 0)
            return null;
        var before = row.Select(takenByOthers).ToArray();
        var after = row.Select((unit, i) => before[i] || picked.Contains(unit)).ToArray();
        var edgeBlocks = rule == SingleSeatRule.BetweenTakenOrRowEnd;
        for (var i = 0; i < row.Count; i++)
        {
            if (after[i] || !IsSingle(after, i, edgeBlocks))
                continue;
            if (!before[i] && IsSingle(before, i, edgeBlocks))
                continue;
            return row[i];
        }
        return null;
    }

    private static bool IsSingle(bool[] occupied, int i, bool edgeBlocks)
    {
        var left = i == 0 ? edgeBlocks : occupied[i - 1];
        var right = i == occupied.Length - 1 ? edgeBlocks : occupied[i + 1];
        return left && right;
    }
}
