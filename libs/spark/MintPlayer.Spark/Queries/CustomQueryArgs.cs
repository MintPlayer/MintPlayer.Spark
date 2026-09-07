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

    private List<string>? _disabledActions;

    /// <summary>Custom actions withheld for this query result. See <see cref="DisableActions"/>.</summary>
    public IReadOnlyList<string>? DisabledActions => _disabledActions;

    /// <summary>
    /// Withholds one or more custom actions from this query's action bar.
    /// <para>
    /// The mirror of <see cref="PersistentObject.DisableActions"/>, and it exists for the same
    /// reason: <c>GET /spark/actions/{objectTypeId}</c> is a type-level catalogue, so an action
    /// that only applies to some results cannot be filtered there. This method is the per-result
    /// answer, decided where the data is in hand.
    /// </para>
    /// <para>
    /// An affordance, not a permission. The action endpoint stays reachable and its handler must
    /// still refuse — this stops an action being <em>offered</em> where it cannot apply.
    /// </para>
    /// </summary>
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
