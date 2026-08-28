using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Queries;

/// <summary>
/// Context passed to a custom query method when executed.
/// </summary>
public sealed class CustomQueryArgs
{
    /// <summary>
    /// The parent PersistentObject (for detail/sub-queries).
    /// Null for top-level queries.
    /// </summary>
    public PersistentObject? Parent { get; set; }

    /// <summary>
    /// The entity type name of the parent (e.g., "Company").
    /// </summary>
    public string? ParentType { get; set; }

    /// <summary>
    /// The SparkQuery being executed (for conditional behavior based on query metadata).
    /// </summary>
    /// <remarks>
    /// Carries the request's sort as <see cref="SparkQuery.SortColumns"/> — the caller's
    /// <c>?sortColumns=</c> override when there was one, the query's declared order otherwise.
    /// </remarks>
    public required SparkQuery Query { get; set; }

    /// <summary>
    /// The request's paging window and search term.
    /// </summary>
    /// <remarks>
    /// A method returning a bare sequence may ignore all three: the framework searches, sorts,
    /// counts and pages what it gets back. A method returning
    /// <see cref="SparkQueryPage{T}"/> takes over all five and must honour them itself — see the
    /// binary authority rule on that type.
    /// </remarks>
    public int Skip { get; set; }

    /// <inheritdoc cref="Skip"/>
    public int Take { get; set; }

    /// <inheritdoc cref="Skip"/>
    public string? Search { get; set; }
}
