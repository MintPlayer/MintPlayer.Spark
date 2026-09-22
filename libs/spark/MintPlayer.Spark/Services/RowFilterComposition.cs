namespace MintPlayer.Spark.Services;

/// <summary>
/// Which branch row-filter composition took for one request, and therefore whether anything can still
/// remove rows after the database has answered (#431 M13).
/// </summary>
/// <remarks>
/// This used to be knowable only from a log line, and only once: the announcement is guarded by a
/// process-wide "have I said this about this type yet" dictionary, so it is diagnostics, not a signal.
/// Paging pushdown needs a per-request answer.
/// </remarks>
public enum RowFilterMode
{
    /// <summary>The system context is not a viewer to scope rows for, so nothing filters.</summary>
    SystemContext,

    /// <summary>
    /// The type declares no row rule. Nothing to push down and nothing to post-filter — the cheapest
    /// safe case, and the most common one.
    /// </summary>
    NoRule,

    /// <summary>
    /// The rule is a constant predicate (the caller may see everything, or nothing). Not translated
    /// into the query; the in-memory post-filter evaluates it instead.
    /// </summary>
    ConstantPredicate,

    /// <summary>
    /// The rule is typed on the entity and the query returns a projection, so it could not compose.
    /// The post-materialization gate is the filter. This is the <b>default</b> shape for an indexed
    /// query, not a corner case.
    /// </summary>
    ProjectionFallback,

    /// <summary>The rule composed into the database query as a <c>Where</c>.</summary>
    PushedDown,
}

/// <summary>
/// The composed queryable plus the branch that produced it.
/// </summary>
/// <param name="Queryable">The queryable, narrowed when — and only when — <see cref="Mode"/> is
/// <see cref="RowFilterMode.PushedDown"/>.</param>
/// <param name="Mode">Which branch ran.</param>
/// <param name="HasPerRowRefinement">
/// Whether the type also overrides <c>IsAllowedAsync</c>, which refines per row <em>after</em>
/// materialization and can therefore still drop rows even on the pushdown path.
/// </param>
public sealed record RowFilterComposition(
    object Queryable,
    RowFilterMode Mode,
    bool HasPerRowRefinement)
{
    /// <summary>
    /// Whether <c>Skip</c>/<c>Take</c> may be issued to the database for this request.
    /// </summary>
    /// <remarks>
    /// The question is not "was the filter pushed down" but the stricter <b>"can anything still remove
    /// rows after the database answers"</b>. If something can, the database's page and the caller's
    /// page are different sets: pages come back short, offsets drift, and <c>TotalItems</c> counts
    /// rows the caller may not see — which is the cardinality oracle this codebase already refused
    /// once, on an author-supplied total.
    /// <para>
    /// So a pushdown with <c>IsAllowedAsync</c> overridden is <b>not</b> safe, even though the
    /// expression composed: the refinement runs per row, later, and drops some.
    /// </para>
    /// <para>
    /// Redaction is irrelevant here — it nulls attributes and never removes a row.
    /// </para>
    /// </remarks>
    public bool CanPageInDatabase => Mode switch
    {
        RowFilterMode.SystemContext => true,
        RowFilterMode.NoRule => !HasPerRowRefinement,
        RowFilterMode.PushedDown => !HasPerRowRefinement,
        _ => false,
    };
}
