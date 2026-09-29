using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Whether a query's grid lets the user select rows, and how many (#460, D17).
/// </summary>
/// <remarks>
/// Declared on the query (<see cref="SparkQuery.SelectionMode"/>) and overridable per sub-query
/// entry (<see cref="SparkSubQuery.SelectionMode"/>). Presentation only: selection is what the grid
/// offers, not what the server accepts — every action that takes rows still enforces its own
/// selection rule and the row gate at submit.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SparkSelectionMode>))]
public enum SparkSelectionMode
{
    /// <summary>
    /// Derived from the actions the query offers: checkboxes appear exactly when some offered action
    /// has a selection rule, single-select when every such rule wants exactly one row. The
    /// behaviour before this setting existed, and the default.
    /// </summary>
    [JsonStringEnumMemberName("auto")] Auto,

    /// <summary>No checkboxes, whatever the actions are. Row menus still act on their own row.</summary>
    [JsonStringEnumMemberName("none")] None,

    /// <summary>At most one selected row.</summary>
    [JsonStringEnumMemberName("single")] Single,

    /// <summary>Any number of selected rows, ticked one by one; there is no select-all.</summary>
    [JsonStringEnumMemberName("multiple")] Multiple,
}
