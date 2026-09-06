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
/// What this hook <i>is</i> for is the per-result answer the action catalogue cannot give:
/// <c>GET /spark/actions/{objectTypeId}</c> is type-level and is never told what an execution
/// returned, so an action that applies to only some results can only be withheld here.
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

    private List<string>? _disabledActions;

    /// <summary>Actions withheld for this result. Null when none were.</summary>
    public IReadOnlyList<string>? DisabledActions => _disabledActions;

    /// <summary>
    /// Withholds one or more custom actions from this query's action bar. Additive and
    /// idempotent, so separate concerns can each withhold what they own without coordinating.
    /// </summary>
    /// <remarks>
    /// An affordance, not a permission. The action endpoint stays reachable and the action's own
    /// handler must still refuse — this stops an action being <em>offered</em> where it cannot
    /// apply.
    /// </remarks>
    public void DisableActions(params string[] actionNames)
    {
        if (actionNames is null || actionNames.Length == 0)
            return;

        _disabledActions ??= [];

        foreach (var name in actionNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && !_disabledActions.Contains(name, StringComparer.OrdinalIgnoreCase))
                _disabledActions.Add(name);
        }
    }
}
