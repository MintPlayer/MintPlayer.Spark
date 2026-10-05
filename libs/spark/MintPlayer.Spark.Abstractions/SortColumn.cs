namespace MintPlayer.Spark.Abstractions;

public sealed class SortColumn
{
    public required string Property { get; set; }

    /// <summary><c>asc</c> or <c>desc</c>, in any case. Anything else is refused.</summary>
    public string Direction { get; set; } = "asc";

    /// <summary>
    /// Whether <paramref name="direction"/> is one of the two spellings Spark accepts.
    /// </summary>
    /// <remarks>
    /// Anything else used to sort ascending without a word, so a caller asking for the newest first
    /// silently got the oldest first. The execute endpoint now answers 400 and the model loader
    /// refuses a declared sort with any other spelling (docs/datetimeoffset_query_sort_filter_PRD.md, D5).
    /// </remarks>
    public static bool IsValidDirection(string? direction)
        => string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase)
        || string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
}
