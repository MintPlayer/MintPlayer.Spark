using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Queries;

/// <summary>
/// Per-request context handed to <c>OnQueryAsync</c>, for shaping how a query's result is
/// <em>presented</em>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not where row security goes.</b> An earlier hook of the same name was removed for
/// exactly that reason, and the reasoning has not changed: a query-level filter guards only the
/// <i>list</i>. A detail read by id runs no query, so a filter never sees it — filter the list to
/// eight cars and a caller can still open, edit or delete car nine by id. Row security belongs in
/// <c>GetRowFilterAsync</c>, which the framework applies to every path (list, detail, edit, delete,
/// create, streaming and breadcrumb loads) from one expression, so they cannot drift apart.
/// </para>
/// <para>
/// Nor is it where actions are withheld any more (#460, D13): that is <c>OnDisableActionsAsync</c>
/// with a query target, which the framework also consults when an action is submitted.
/// </para>
/// <para>
/// It is deliberately a context object rather than the <see cref="SparkQuery"/> itself.
/// <c>ModelLoader</c> is a singleton and hands out the <b>shared</b> query instance, so withholding
/// an action on it would withhold that action for every subsequent request, for every user, until
/// the process restarted — and would look like an intermittent caching bug rather than what it is.
/// <see cref="Query"/> is a <see cref="SparkQueryInfo"/> for the same reason: an earlier revision
/// exposed the <see cref="SparkQuery"/> with a comment calling it read-only, which was false —
/// <c>init</c> freezes the reference, not the object, and every property on it has a public setter.
/// </para>
/// </remarks>
public sealed class SparkQueryContext
{
    /// <summary>The query being executed, as an immutable view. See <see cref="SparkQueryInfo"/>.</summary>
    public required SparkQueryInfo Query { get; init; }

    /// <summary>The parent object for a detail/sub-query, null for a top-level query.</summary>
    public PersistentObject? Parent { get; init; }

    /// <summary>The entity type name of the parent, null for a top-level query.</summary>
    public string? ParentType { get; init; }

    // DisableActions / DisabledActions are deleted (#460, D13). Withholding an action is
    // OnDisableActionsAsync's job now, with a Query target, because that hook is also asked when an
    // action is submitted — an answer given only here could never be enforced.
}
