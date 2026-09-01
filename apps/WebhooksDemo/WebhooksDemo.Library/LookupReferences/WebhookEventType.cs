using MintPlayer.Spark.Abstractions;

namespace WebhooksDemo.LookupReferences;

public enum EWebhookEventType
{
    // Issues
    IssuesOpened,
    IssuesClosed,
    IssuesReopened,
    IssuesLabeled,
    IssuesUnlabeled,
    IssuesAssigned,
    IssuesUnassigned,

    // Pull Requests
    PullRequestOpened,
    PullRequestClosed,
    PullRequestMerged,
    PullRequestReadyForReview,
    PullRequestConvertedToDraft,
    PullRequestReviewRequested,

    // Pull Request Reviews
    PullRequestReviewApproved,
    PullRequestReviewChangesRequested,
    PullRequestReviewDismissed,

    // Check Runs
    CheckRunCompleted,

    // Issue Comments
    IssueCommentCreated,
}

public sealed class WebhookEventType : TransientLookupReference<EWebhookEventType>
{
    private WebhookEventType() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    /// <summary>
    /// The Octokit event type name (matches WebhookHeaders.Event).
    /// </summary>
    public string EventName { get; init; } = string.Empty;

    /// <summary>
    /// The Octokit action value (matches the event's Action property).
    /// Null means "any action" (for events without sub-actions).
    /// </summary>
    public string? ActionValue { get; init; }

    public static IReadOnlyCollection<WebhookEventType> Items { get; } =
    [
        // Issues
        new() { Key = EWebhookEventType.IssuesOpened,     EventName = "issues", ActionValue = "opened",     Values = _TS("Issue opened") },
        new() { Key = EWebhookEventType.IssuesClosed,     EventName = "issues", ActionValue = "closed",     Values = _TS("Issue closed") },
        new() { Key = EWebhookEventType.IssuesReopened,   EventName = "issues", ActionValue = "reopened",   Values = _TS("Issue reopened") },
        new() { Key = EWebhookEventType.IssuesLabeled,    EventName = "issues", ActionValue = "labeled",    Values = _TS("Issue labeled") },
        new() { Key = EWebhookEventType.IssuesUnlabeled,  EventName = "issues", ActionValue = "unlabeled",  Values = _TS("Issue unlabeled") },
        new() { Key = EWebhookEventType.IssuesAssigned,   EventName = "issues", ActionValue = "assigned",   Values = _TS("Issue assigned") },
        new() { Key = EWebhookEventType.IssuesUnassigned, EventName = "issues", ActionValue = "unassigned", Values = _TS("Issue unassigned") },

        // Pull Requests
        new() { Key = EWebhookEventType.PullRequestOpened,           EventName = "pull_request", ActionValue = "opened",             Values = _TS("Pull request opened") },
        new() { Key = EWebhookEventType.PullRequestClosed,           EventName = "pull_request", ActionValue = "closed",             Values = _TS("Pull request closed") },
        new() { Key = EWebhookEventType.PullRequestMerged,           EventName = "pull_request", ActionValue = "closed",             Values = _TS("Pull request merged") },
        new() { Key = EWebhookEventType.PullRequestReadyForReview,   EventName = "pull_request", ActionValue = "ready_for_review",   Values = _TS("PR ready for review") },
        new() { Key = EWebhookEventType.PullRequestConvertedToDraft, EventName = "pull_request", ActionValue = "converted_to_draft", Values = _TS("PR converted to draft") },
        new() { Key = EWebhookEventType.PullRequestReviewRequested,  EventName = "pull_request", ActionValue = "review_requested",   Values = _TS("PR review requested") },

        // Pull Request Reviews
        new() { Key = EWebhookEventType.PullRequestReviewApproved,         EventName = "pull_request_review", ActionValue = "submitted", Values = _TS("PR review: approved") },
        new() { Key = EWebhookEventType.PullRequestReviewChangesRequested, EventName = "pull_request_review", ActionValue = "submitted", Values = _TS("PR review: changes requested") },
        new() { Key = EWebhookEventType.PullRequestReviewDismissed,        EventName = "pull_request_review", ActionValue = "dismissed", Values = _TS("PR review dismissed") },

        // Check Runs
        new() { Key = EWebhookEventType.CheckRunCompleted, EventName = "check_run", ActionValue = "completed", Values = _TS("Check run completed") },

        // Issue Comments
        new() { Key = EWebhookEventType.IssueCommentCreated, EventName = "issue_comment", ActionValue = "created", Values = _TS("Issue comment created") },
    ];
}
