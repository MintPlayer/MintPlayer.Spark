using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Dashboards;

/// <summary>
/// One point of a value's history, as a <c>{Value}History</c> attribute carries it for <c>spark-trend</c>: the
/// bucket's start (<c>x</c>, epoch milliseconds, UTC) and its average (<c>y</c>, null for an empty bucket).
/// </summary>
public sealed record DashboardHistoryPoint(
    [property: JsonPropertyName("x")] long X,
    [property: JsonPropertyName("y")] double? Y);
