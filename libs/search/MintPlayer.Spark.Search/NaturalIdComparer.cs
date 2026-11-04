namespace MintPlayer.Spark.Search;

/// <summary>
/// Orders ids the way a person counts: <c>songs/2-A</c> before <c>songs/10-A</c>. Namesakes are numbered in this order
/// (<c>docs/spark_search_PRD.md</c> D3), so "Song 1" stays the oldest of the RavenDB ids however many follow.
/// </summary>
internal sealed class NaturalIdComparer : IComparer<string>
{
    public static readonly NaturalIdComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i; while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                var sj = j; while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var digits = a.SequenceCompareTo(b);
                if (digits != 0) return digits;
                continue;
            }
            var c = string.CompareOrdinal(x, i, y, j, 1);
            if (c != 0) return c;
            i++; j++;
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
