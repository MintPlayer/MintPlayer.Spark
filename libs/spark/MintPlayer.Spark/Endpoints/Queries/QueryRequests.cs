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

    /// <summary>The object whose detail page a sub-query was rendered on, by id and type.</summary>
    public string? ParentId { get; set; }

    /// <inheritdoc cref="ParentId" />
    public string? ParentType { get; set; }

    /// <inheritdoc />
    public RetryResult[]? RetryResults { get; set; }
}
