namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// One selectable entry in a column's filter panel (#431).
/// </summary>
/// <remarks>
/// <b>A pair, not a string.</b> <see cref="Value"/> is the raw stored value and is the only thing
/// that travels back in <see cref="QueryColumnFilter"/>; <see cref="Label"/> is presentation only.
/// For a reference column the value is a document id and the label is resolved breadcrumb text, and
/// they are never the same string.
/// <para>
/// Keeping them separate is also what lets two values relabel to the same text without making the
/// predicate ambiguous — the protocol this follows packs both into one length-prefixed string, which
/// works but makes every consumer parse it.
/// </para>
/// </remarks>
public sealed class DistinctValue
{
    /// <summary>The raw stored value. Null is a real entry meaning "no value", not an absent one.</summary>
    public object? Value { get; init; }

    /// <summary>What the user reads. Never matched on.</summary>
    public required string Label { get; init; }
}

/// <summary>
/// A column's distinct values, split into the two buckets a filter panel renders differently.
/// </summary>
/// <remarks>
/// <b><see cref="Remaining"/> is returned empty, deliberately.</b> The client component snapshots the
/// value list when a selection is first made and derives both buckets itself by diffing that snapshot
/// against each freshly loaded list. The server's job is to answer "what matches now"; remembering
/// what used to match is the panel's.
/// <para>
/// <b><see cref="HasMore"/> must be honest.</b> The panel re-queries only when it is set or when the
/// search term widens, so reporting <see langword="false"/> on a truncated list strands a user typing
/// past the cap with stale values.
/// </para>
/// </remarks>
public sealed class DistinctValuesResult
{
    /// <summary>Values present under the current filter context and search term, capped.</summary>
    public required IReadOnlyList<DistinctValue> Matching { get; init; }

    /// <summary>Always empty from this server. See the remarks.</summary>
    public IReadOnlyList<DistinctValue> Remaining { get; init; } = [];

    /// <summary>Whether the cap truncated <see cref="Matching"/>.</summary>
    public required bool HasMore { get; init; }

    /// <summary>
    /// The answer for a column that may not be enumerated, and for one that matched nothing.
    /// </summary>
    /// <remarks>
    /// The same value for both, on purpose: a caller must not be able to tell "you may not list this"
    /// from "there is nothing to list". The first is a fact about their rights, and telling them
    /// would be the disclosure the flag exists to prevent.
    /// </remarks>
    public static DistinctValuesResult Empty { get; } = new() { Matching = [], HasMore = false };
}
