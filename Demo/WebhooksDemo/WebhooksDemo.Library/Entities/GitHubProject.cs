namespace WebhooksDemo.Entities;

public class GitHubProject
{
    public string? Id { get; set; }

    /// <summary>Display name of the GitHub project.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>GitHub GraphQL node ID (e.g., "PVT_kwDO...").</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Owner login (user or organization).</summary>
    public string OwnerLogin { get; set; } = string.Empty;

    /// <summary>Project number (visible in GitHub URL).</summary>
    public int Number { get; set; }

    /// <summary>GraphQL ID of the "Status" single-select field.</summary>
    public string StatusFieldId { get; set; } = string.Empty;

    /// <summary>
    /// Cached column options from the project's Status field.
    /// Synced when the project is added or refreshed.
    /// </summary>
    public ProjectColumn[] Columns { get; set; } = [];

    /// <summary>
    /// User-configured rules: which webhook events move issues to which columns.
    /// </summary>
    public EventColumnMapping[] EventMappings { get; set; } = [];
}
