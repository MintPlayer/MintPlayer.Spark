using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// The GitHub events a board automation rule can react to.
/// </summary>
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

/// <summary>
/// The closed set of GitHub events a board rule may fire on, with the wire values needed to match a
/// delivery against it.
/// <para>
/// A lookup rather than a free-text field because the set is closed and a typo would produce a rule
/// that silently never fires — indistinguishable, from the UI, from a rule that fires and does
/// nothing.
/// </para>
/// <para>
/// <b>Matching is not a simple pair lookup, and two entries are why.</b>
/// <see cref="EWebhookEventType.PullRequestClosed"/> and
/// <see cref="EWebhookEventType.PullRequestMerged"/> share the same
/// <see cref="EventName"/>/<see cref="ActionValue"/> — GitHub reports a merge as
/// <c>pull_request.closed</c> and distinguishes it only by the payload's <c>merged</c> flag.
/// Likewise <see cref="EWebhookEventType.PullRequestReviewApproved"/> and
/// <see cref="EWebhookEventType.PullRequestReviewChangesRequested"/> both arrive as
/// <c>pull_request_review.submitted</c>, separated only by the review's state. So a resolver that
/// keys on (event, action) alone maps a merge onto the "closed" rule and drops the "merged" one —
/// see <c>ProjectAutomationRecipient</c>, which disambiguates on the payload.
/// </para>
/// </summary>
public sealed class WebhookEventType : TransientLookupReference<EWebhookEventType>
{
    private WebhookEventType() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    /// <summary>The GitHub event name, as sent in <c>X-GitHub-Event</c> and recorded on the message envelope.</summary>
    public string EventName { get; init; } = string.Empty;

    /// <summary>
    /// The payload's <c>action</c> value. Null would mean "any action"; every entry here has one,
    /// because every event in this set carries a sub-action.
    /// </summary>
    public string? ActionValue { get; init; }

    /// <summary>
    /// Every event a rule may target. <b>Eighteen</b> entries — the PRD and plan for this feature
    /// both said nineteen, which was simply a miscount of this same list and is corrected here.
    /// </summary>
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

        // Pull Requests. Closed and Merged deliberately share event+action; see the class remarks.
        new() { Key = EWebhookEventType.PullRequestOpened,           EventName = "pull_request", ActionValue = "opened",             Values = _TS("Pull request opened") },
        new() { Key = EWebhookEventType.PullRequestClosed,           EventName = "pull_request", ActionValue = "closed",             Values = _TS("Pull request closed without merging") },
        new() { Key = EWebhookEventType.PullRequestMerged,           EventName = "pull_request", ActionValue = "closed",             Values = _TS("Pull request merged") },
        new() { Key = EWebhookEventType.PullRequestReadyForReview,   EventName = "pull_request", ActionValue = "ready_for_review",   Values = _TS("PR ready for review") },
        new() { Key = EWebhookEventType.PullRequestConvertedToDraft, EventName = "pull_request", ActionValue = "converted_to_draft", Values = _TS("PR converted to draft") },
        new() { Key = EWebhookEventType.PullRequestReviewRequested,  EventName = "pull_request", ActionValue = "review_requested",   Values = _TS("PR review requested") },

        // Pull Request Reviews. Approved and ChangesRequested share event+action; see the remarks.
        new() { Key = EWebhookEventType.PullRequestReviewApproved,         EventName = "pull_request_review", ActionValue = "submitted", Values = _TS("PR review: approved") },
        new() { Key = EWebhookEventType.PullRequestReviewChangesRequested, EventName = "pull_request_review", ActionValue = "submitted", Values = _TS("PR review: changes requested") },
        new() { Key = EWebhookEventType.PullRequestReviewDismissed,        EventName = "pull_request_review", ActionValue = "dismissed", Values = _TS("PR review dismissed") },

        // Check Runs
        new() { Key = EWebhookEventType.CheckRunCompleted, EventName = "check_run", ActionValue = "completed", Values = _TS("Check run completed") },

        // Issue Comments
        new() { Key = EWebhookEventType.IssueCommentCreated, EventName = "issue_comment", ActionValue = "created", Values = _TS("Issue comment created") },
    ];
}
