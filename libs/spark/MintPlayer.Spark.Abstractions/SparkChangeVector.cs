using System.Globalization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>How one change vector relates to another (<see cref="SparkChangeVector.Compare"/>).</summary>
public enum SparkChangeVectorOrder
{
    /// <summary>The first is an earlier version of the document than the second.</summary>
    Older,

    /// <summary>The same version.</summary>
    Equal,

    /// <summary>The first is a later version than the second.</summary>
    Newer,

    /// <summary>
    /// Neither contains the other: two nodes of a cluster changed the document independently (a RavenDB conflict).
    /// Only a conflicted multi-node database produces it.
    /// </summary>
    Concurrent,
}

/// <summary>
/// RavenDB change vectors as version stamps (warehouse WD18). A change vector is a list of <c>TAG:etag-dbid</c>
/// entries, one per database node that changed the document; a later version of a document has, for every database
/// id, an etag at least as high. Replication compares the source change vector a replica was built from with the one
/// a push carries, so a late, duplicated or reordered push never replaces a newer replica.
/// </summary>
public static class SparkChangeVector
{
    /// <summary>
    /// How <paramref name="first"/> relates to <paramref name="second"/>, entry-wise per database id. A null or empty
    /// vector is older than any other, and equal to another empty one.
    /// </summary>
    /// <exception cref="FormatException">An entry is not <c>TAG:etag-dbid</c>.</exception>
    public static SparkChangeVectorOrder Compare(string? first, string? second)
    {
        var a = Parse(first);
        var b = Parse(second);
        var aAhead = false;
        var bAhead = false;

        foreach (var (dbId, etag) in a)
        {
            var other = b.TryGetValue(dbId, out var value) ? value : 0;
            if (etag > other) aAhead = true;
            else if (etag < other) bAhead = true;
        }

        foreach (var (dbId, etag) in b)
        {
            if (!a.ContainsKey(dbId) && etag > 0)
                bAhead = true;
        }

        return (aAhead, bAhead) switch
        {
            (false, false) => SparkChangeVectorOrder.Equal,
            (true, false) => SparkChangeVectorOrder.Newer,
            (false, true) => SparkChangeVectorOrder.Older,
            _ => SparkChangeVectorOrder.Concurrent,
        };
    }

    /// <summary>The etag per database id of <paramref name="changeVector"/>; the highest one when a database id repeats.</summary>
    /// <exception cref="FormatException">An entry is not <c>TAG:etag-dbid</c>.</exception>
    public static IReadOnlyDictionary<string, long> Parse(string? changeVector)
    {
        var entries = new Dictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(changeVector))
            return entries;

        foreach (var raw in changeVector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = raw.IndexOf(':');
            var dash = raw.IndexOf('-', colon + 1);
            if (colon <= 0 || dash <= colon + 1 || dash == raw.Length - 1
                || !long.TryParse(raw.AsSpan(colon + 1, dash - colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var etag))
                throw new FormatException($"'{raw}' is not a change vector entry (TAG:etag-dbid).");

            var dbId = raw[(dash + 1)..];
            entries[dbId] = entries.TryGetValue(dbId, out var seen) ? Math.Max(seen, etag) : etag;
        }

        return entries;
    }
}
