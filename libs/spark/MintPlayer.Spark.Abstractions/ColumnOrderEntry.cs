namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// One entry of a query's <see cref="SparkQuery.ColumnOrder"/>: an attribute name, and whether the
/// <c>!</c> prefix lists it hidden (#284).
/// </summary>
/// <remarks>
/// One parser for the runtime, the rights stripping, the synchronizer and verify, so that none of them
/// reads the <c>!</c> marker differently. Names compare case-insensitively everywhere the list is
/// matched against attributes, as the sort allow-list does.
/// </remarks>
public readonly record struct ColumnOrderEntry(string Name, bool Hidden)
{
    /// <summary>The marker that lists a column hidden.</summary>
    public const char HiddenPrefix = '!';

    /// <summary>Splits one raw entry into its name and hidden flag. Never throws; verify reports malformed entries.</summary>
    public static ColumnOrderEntry Parse(string? raw)
    {
        raw ??= string.Empty;
        return raw.Length > 0 && raw[0] == HiddenPrefix
            ? new ColumnOrderEntry(raw[1..], true)
            : new ColumnOrderEntry(raw, false);
    }

    /// <summary>Every entry of <paramref name="columnOrder"/>, in list order; empty for null.</summary>
    public static IReadOnlyList<ColumnOrderEntry> ParseAll(string[]? columnOrder)
        => columnOrder is null ? [] : [.. columnOrder.Select(Parse)];

    /// <summary>The raw form written back to a model file.</summary>
    public override string ToString() => Hidden ? HiddenPrefix + Name : Name;
}
