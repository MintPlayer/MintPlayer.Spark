using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Models;

/// <summary>
/// Metadata for a custom action, loaded from App_Data/customActions.json.
/// </summary>
public class CustomActionDefinition
{
    public required TranslatedString DisplayName { get; set; }
    public string? Icon { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Where the action is shown: "detail", "query", or "both".
    /// </summary>
    public string ShowedOn { get; set; } = "both";

    /// <summary>
    /// Selection rule for query-view actions: "=0" (none), "=1" (exactly one), ">0" (one or more).
    /// </summary>
    public string? SelectionRule { get; set; }

    /// <summary>
    /// Whether the frontend should refresh after execution.
    /// </summary>
    public bool RefreshOnCompleted { get; set; }

    /// <summary>
    /// Translation key for a confirmation dialog before execution.
    /// </summary>
    public string? ConfirmationMessageKey { get; set; }

    /// <summary>
    /// Display order (lower = first).
    /// </summary>
    public int Offset { get; set; }
}
