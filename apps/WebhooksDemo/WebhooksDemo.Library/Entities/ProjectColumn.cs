namespace WebhooksDemo.Entities;

public class ProjectColumn
{
    /// <summary>Exposes OptionId as the identifier for Spark reference pickers.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id => OptionId;

    /// <summary>GitHub single-select option ID (e.g., "f75ad846").</summary>
    public string OptionId { get; set; } = string.Empty;

    /// <summary>Display name (e.g., "Todo", "In Progress", "Done").</summary>
    public string Name { get; set; } = string.Empty;
}
