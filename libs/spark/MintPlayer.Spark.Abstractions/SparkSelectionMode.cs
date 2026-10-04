using System.Text.Json.Serialization;

namespace MintPlayer.Spark.Abstractions;

/// <summary>
/// Whether a query's grid lets the user select rows (#460 D17, #467 R1/D10).
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
    /// Derived from the actions the query offers (#467, R1): checkboxes appear exactly when some
    /// action the caller may use on this list has a selection rule that accepts at least one row —
    /// built-in Edit and Delete included. The default.
    /// </summary>
    [JsonStringEnumMemberName("auto")] Auto,

    /// <summary>No checkboxes, whatever the actions are. Row menus still act on their own row.</summary>
    [JsonStringEnumMemberName("none")] None,

    /// <summary>
    /// Any number of selected rows, ticked one by one; there is no select-all. Actions whose rule
    /// does not match the count are disabled, and the selection is never trimmed (#467, D10).
    /// </summary>
    [JsonStringEnumMemberName("multiple")] Multiple,
}
