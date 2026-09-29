using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Which rows a query asks for with respect to soft deletion (#460, T2) — the <c>deleted</c> field
/// of a query request (<c>"exclude"</c>, <c>"include"</c>, <c>"only"</c>).
/// </summary>
/// <remarks>
/// <para>
/// Core only <b>carries</b> the flag: it reaches every row policy through
/// <c>RowPolicyContext.Deleted</c>. Core itself knows nothing about deletion. The policy that acts
/// on it (the SoftDelete package's) decides whether the caller may widen the view — it is honoured
/// only for holders of <c>ViewDeleted/T</c>, and <see cref="Exclude"/> for everyone else.
/// </para>
/// <para>
/// A field on the request rather than a header, because a header is invisible in the request
/// contract.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SparkDeletedFilter>))]
public enum SparkDeletedFilter
{
    /// <summary>Deleted rows are not returned. The default, and the only mode without <c>ViewDeleted</c>.</summary>
    [JsonStringEnumMemberName("exclude")]
    Exclude = 0,

    /// <summary>Deleted and live rows alike.</summary>
    [JsonStringEnumMemberName("include")]
    Include,

    /// <summary>Only deleted rows — the recycle bin.</summary>
    [JsonStringEnumMemberName("only")]
    Only,
}
