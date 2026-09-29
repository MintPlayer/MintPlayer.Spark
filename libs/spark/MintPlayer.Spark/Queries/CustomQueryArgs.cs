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

    // DisableActions / DisabledActions are deleted (#460, D13). A withhold decided here, with rows in
    // hand, could never be re-derived when the action is submitted, so it could never be enforced.
    // Withhold in OnDisableActionsAsync with a query target instead.

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

    /// <summary>
    /// The request's per-column value filters (#431), or <see langword="null"/> when none were sent.
    /// </summary>
    /// <remarks>
    /// Here for the same reason <see cref="Search"/> is, and with the same authority rule. A method
    /// returning a bare sequence may ignore this: the framework composes the filters onto whatever
    /// it gets back, into RQL when the result is Raven-backed and in process otherwise.
    /// <para>
    /// A method returning <see cref="SparkQueryPage{T}"/> has taken over filtering along with
    /// paging and counting, so the framework does <b>not</b> apply these — it cannot, without
    /// narrowing a page whose total it did not compute, which is the half-delegated failure the
    /// binary authority rule exists to prevent. Such a method must honour them itself, and this is
    /// where it reads them.
    /// </para>
    /// <para>
    /// Columns AND together; the values within one column OR together — the same shape the wire
    /// uses, so an author implementing it by hand matches what the framework would have done.
    /// </para>
    /// </remarks>
    public IReadOnlyList<QueryColumnFilter>? Columns { get; set; }
}
