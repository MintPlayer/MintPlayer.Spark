using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Endpoints.Queries;

/// <summary>The body of <c>POST /spark/queries/get</c>.</summary>
internal sealed class GetQueryRequest
{
    /// <summary>The query, by id or alias.</summary>
    public string? QueryId { get; set; }
}

/// <summary>The body of <c>POST /spark/queries/execute</c>.</summary>
/// <remarks>
/// <para>
/// These were query-string parameters on a <c>GET</c>. They are typed fields now, which is the point
/// of the change and not a side effect of it: column filtering — multiple columns, multiple selected
/// values per column — cannot be expressed as a flat string without inventing an encoding, and
/// inventing one is what <c>sortColumns</c> already did.
/// </para>
/// <para>
/// ⚠️ <c>sortColumns</c> was <c>prop:asc,other:desc</c> on the wire. It is a real array now and the
/// string form is gone rather than accepted as an alias — an alias would hide exactly the callers the
/// migration needs to find.
/// </para>
/// </remarks>
internal sealed class ExecuteQueryRequest : IRetryableRequest
{
    /// <summary>The query, by id or alias.</summary>
    public string? QueryId { get; set; }

    /// <summary>Sort overrides, checked against the query's declared attribute set.</summary>
    public SortColumn[]? SortColumns { get; set; }

    public int? Skip { get; set; }

    public int? Take { get; set; }

    public string? Search { get; set; }

    /// <summary>
    /// Per-column value filters (#431). Columns AND together; values within a column OR together.
    /// </summary>
    /// <remarks>
    /// This is the shape that could not be expressed as a query string, and the reason the reads
    /// moved to POST — see the class remarks.
    /// </remarks>
    public QueryColumnFilter[]? Columns { get; set; }

    /// <summary>The object whose detail page a sub-query was rendered on, by id and type.</summary>
    public string? ParentId { get; set; }

    /// <inheritdoc cref="ParentId" />
    public string? ParentType { get; set; }

    /// <summary>
    /// Whether deleted rows are excluded (default), included, or the only rows (#460, T2). Carried to
    /// every row policy through <c>RowPolicyContext.Deleted</c>; honoured by the SoftDelete package's
    /// policy only for holders of <c>ViewDeleted/T</c>. Core itself filters nothing on it.
    /// </summary>
    public SparkDeletedFilter? Deleted { get; set; }

    /// <summary>
    /// The soft-deletion mode the PARENT (<see cref="ParentId"/>) is resolved under (#460) — set to
    /// <c>include</c> by a sub-query on a detail page opened from the recycle bin. Independent of
    /// <see cref="Deleted"/>, which stays the rows' own filter. Like <see cref="Deleted"/> it is only
    /// carried to the row policies: the SoftDelete package honours it for holders of
    /// <c>ViewDeleted/{ParentType}</c>, and everyone else gets the same 404 a missing parent gives.
    /// </summary>
    /// <remarks>
    /// Only the two query reads take it. <c>/spark/actions/execute</c> and <c>/spark/po/delete-many</c>
    /// resolve their sub-query parent as a live row, always: the recycle bin offers no actions on a
    /// deleted object's page, so a deleted parent there is refused, not widened.
    /// </remarks>
    public SparkDeletedFilter? ParentDeleted { get; set; }

    /// <inheritdoc />
    public RetryResult[]? RetryResults { get; set; }
}

/// <summary>The body of <c>POST /spark/queries/distinct-values</c> (#431).</summary>
internal sealed class DistinctValuesRequest
{
    /// <summary>The query, by id or alias.</summary>
    public string? QueryId { get; set; }

    /// <summary>The column whose values are being listed.</summary>
    public string? Column { get; set; }

    /// <summary>Narrows the returned values. Round-trips, because the list is capped server-side.</summary>
    public string? Search { get; set; }

    /// <summary>
    /// The grid's own search term — what <c>/execute</c> takes as <c>search</c> (#460 M15). The values
    /// are drawn only from the rows it matches, so a searched grid's panel offers what the grid shows.
    /// Distinct from <see cref="Search"/>, which narrows the listed values themselves.
    /// </summary>
    public string? QuerySearch { get; set; }

    /// <summary>
    /// The other columns' current filters, so the values returned are the ones still reachable.
    /// </summary>
    /// <remarks>
    /// Without this the panel would offer values that yield an empty grid the moment they are picked,
    /// because another column has already excluded every row carrying them.
    /// </remarks>
    public QueryColumnFilter[]? Columns { get; set; }

    /// <summary>The object whose detail page a sub-query was rendered on, by id and type.</summary>
    public string? ParentId { get; set; }

    /// <inheritdoc cref="ParentId" />
    public string? ParentType { get; set; }

    /// <summary>The same soft-deletion mode as the grid the panel belongs to (#460, T2).</summary>
    public SparkDeletedFilter? Deleted { get; set; }

    /// <inheritdoc cref="ExecuteQueryRequest.ParentDeleted" />
    public SparkDeletedFilter? ParentDeleted { get; set; }
}
