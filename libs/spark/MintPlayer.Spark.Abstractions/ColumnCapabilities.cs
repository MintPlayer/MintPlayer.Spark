namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Resolves a column's <c>canSort</c> / <c>canFilter</c> / <c>canListDistincts</c> from the query's
/// sparse override then the attribute, defaulting to capable (#431).
/// </summary>
/// <remarks>
/// One place, because the answer is needed twice and the two must not drift: the projector writes it
/// onto the wire column so the grid knows what to draw, and the executor checks it so a caller who
/// bypasses the grid is refused. A flag that decorated the UI while the endpoint accepted anything
/// would be the same non-enforcement the <c>?sortColumns=</c> hardening (#294-#296) was about.
/// <para>
/// <b>Resolution order is override → attribute → true.</b> Null at every level means "not stated
/// here", which is why the model types use <c>bool?</c>: "absent" and "false" are different answers
/// and only the first defers outward.
/// </para>
/// <para>
/// <b>Never writes back.</b> The resolved answer belongs on the per-request <see cref="QueryColumn"/>,
/// never on the <see cref="EntityAttributeDefinition"/> — those are handed out by reference from a
/// singleton model loader, and a per-caller value written there leaks process-wide. That bug has
/// already shipped once here, on a per-caller <c>CanRead</c>.
/// </para>
/// </remarks>
public static class ColumnCapabilities
{
    /// <summary>The query's override entry for <paramref name="attributeName"/>, if it has one.</summary>
    public static SparkQueryColumn? FindOverride(SparkQuery? query, string attributeName)
    {
        if (query?.Columns is not { Length: > 0 } columns) return null;

        foreach (var column in columns)
        {
            if (string.Equals(column.Name, attributeName, StringComparison.OrdinalIgnoreCase))
                return column;
        }

        return null;
    }

    /// <summary>An embedded object rather than a value: <c>AsDetail</c>.</summary>
    /// <remarks>
    /// Such a column has no scalar term in the index — the generator emits it at
    /// <c>FieldIndexing.No</c> — and no scalar value on the wire either: <c>EntityMapper</c> sets the
    /// attribute's value to null and carries the detail separately. So every question a grid can ask
    /// about it has no answer, and the model's default of "capable" is wrong for all three.
    /// </remarks>
    private static bool IsEmbedded(EntityAttributeDefinition attribute)
        => string.Equals(attribute.DataType, "AsDetail", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc cref="EntityAttributeDefinition.CanSort"/>
    /// <remarks>
    /// ⚠️ <b>Ordering by a collection does not merely produce an arbitrary order — it DROPS ROWS.</b>
    /// Measured on 7.2.6: over four documents, one with an empty collection, <c>OrderBy</c> returned
    /// three and <c>OrderByDescending</c> returned the same three in the same order. Nullable scalar
    /// controls behaved correctly in the same run (four rows, exact reverse), so this is a property of
    /// collection-ness, not of a missing term. A result set that changes size because the user clicked
    /// a column header is a correctness defect, so the refusal is structural rather than something a
    /// model author has to remember — 16 array attributes sit on a query surface today with nothing
    /// authored, i.e. all resolving to <see langword="true"/>.
    /// <para>
    /// An <see cref="IsEmbedded"/> column is refused for a different reason: it is indexed
    /// <c>FieldIndexing.No</c>, which degrades <em>silently</em> — the sort is a no-op and the grid
    /// simply does not reorder, with a 200 and no diagnostic.
    /// </para>
    /// </remarks>
    public static bool CanSort(EntityAttributeDefinition attribute, SparkQuery? query)
        => !attribute.IsArray
        && !IsEmbedded(attribute)
        && (FindOverride(query, attribute.Name)?.CanSort ?? attribute.CanSort ?? true);

    /// <inheritdoc cref="EntityAttributeDefinition.CanFilter"/>
    /// <remarks>
    /// A streaming query can never be filtered, whatever the model says, and the refusal belongs here
    /// rather than in a grid template so that the server states it once for every client.
    /// <para>
    /// <c>ExecuteStreamingQueryAsync</c> takes no filters and has nowhere to receive them — the socket
    /// handshake carries no body — while these flags default to <see langword="true"/>. The result was
    /// a filter button on every column of every streaming grid, whose panel 500ed (the distincts
    /// endpoint re-executes the query, and a streaming method's signature is one
    /// <c>ResolveCustomQueryMethod</c> cannot accept) and whose ticked value did nothing at all.
    /// </para>
    /// <para>
    /// Sorting is deliberately not refused here: the client sorts the accumulated snapshot itself.
    /// </para>
    /// </remarks>
    public static bool CanFilter(EntityAttributeDefinition attribute, SparkQuery? query)
        => query is not { IsStreamingQuery: true }
        && !IsEmbedded(attribute)
        && (FindOverride(query, attribute.Name)?.CanFilter ?? attribute.CanFilter ?? true);

    /// <inheritdoc cref="EntityAttributeDefinition.CanListDistincts"/>
    /// <remarks>Refused for a streaming query for the reasons on <see cref="CanFilter"/>.</remarks>
    public static bool CanListDistincts(EntityAttributeDefinition attribute, SparkQuery? query)
        => query is not { IsStreamingQuery: true }
        && !IsEmbedded(attribute)
        && (FindOverride(query, attribute.Name)?.CanListDistincts ?? attribute.CanListDistincts ?? true);

    /// <summary>
    /// The attribute named <paramref name="attributeName"/> on the query surface, or null when it is
    /// not on it at all.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, matching the sort allow-list: a caller naming <c>lastname</c> for
    /// <c>LastName</c> is a spelling difference, not an attempt at something.
    /// </remarks>
    public static EntityAttributeDefinition? FindQuerySurfaceAttribute(
        EntityTypeDefinition definition,
        string attributeName)
    {
        foreach (var attribute in definition.Attributes)
        {
            if (string.Equals(attribute.Name, attributeName, StringComparison.OrdinalIgnoreCase)
                && attribute.ShowedOn.HasFlag(EShowedOn.Query))
            {
                return attribute;
            }
        }

        return null;
    }
}
