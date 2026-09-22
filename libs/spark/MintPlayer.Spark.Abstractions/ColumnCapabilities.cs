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

    /// <inheritdoc cref="EntityAttributeDefinition.CanSort"/>
    public static bool CanSort(EntityAttributeDefinition attribute, SparkQuery? query)
        => FindOverride(query, attribute.Name)?.CanSort ?? attribute.CanSort ?? true;

    /// <inheritdoc cref="EntityAttributeDefinition.CanFilter"/>
    public static bool CanFilter(EntityAttributeDefinition attribute, SparkQuery? query)
        => FindOverride(query, attribute.Name)?.CanFilter ?? attribute.CanFilter ?? true;

    /// <inheritdoc cref="EntityAttributeDefinition.CanListDistincts"/>
    public static bool CanListDistincts(EntityAttributeDefinition attribute, SparkQuery? query)
        => FindOverride(query, attribute.Name)?.CanListDistincts ?? attribute.CanListDistincts ?? true;

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
