using MintPlayer.Spark.Abstractions;
using WebhooksDemo.LookupReferences;

namespace WebhooksDemo.Entities;

public class EventColumnMapping
{
    /// <summary>The webhook event type (e.g., "IssuesOpened").</summary>
    [LookupReference(typeof(WebhookEventType))]
    public string? WebhookEvent { get; set; }

    /// <summary>The target column option ID on the project board.</summary>
    [ContextualLookupReference("Columns", "OptionId", "Name")]
    public string? TargetColumnOptionId { get; set; }

    /// <summary>
    /// For pull request events: also move the issues that the PR closes/references.
    /// Ignored for issue events.
    /// </summary>
    public bool MoveLinkedIssues { get; set; }
}
