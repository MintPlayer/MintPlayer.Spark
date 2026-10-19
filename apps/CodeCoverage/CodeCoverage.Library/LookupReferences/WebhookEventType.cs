using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// The GitHub events a board automation rule can react to. <c>[SparkLookup]</c> generates the
/// <see cref="WebhookEventType"/> lookup; its labels are the <c>translations.json</c> keys
/// <c>lookup.EWebhookEventType.*</c>.
/// </summary>
[SparkLookup]
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
/// delivery against it. The items themselves are generated from <see cref="EWebhookEventType"/>; this
/// part adds the wire values.
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
/// <para>
/// <b>Eighteen</b> entries — the PRD and plan for this feature both said nineteen, which was simply a
/// miscount of this same list and is corrected here.
/// </para>
/// </summary>
public sealed partial class WebhookEventType
{
    /// <summary>The GitHub event name, as sent in <c>X-GitHub-Event</c> and recorded on the message envelope.</summary>
    public string EventName { get; private set; } = string.Empty;

    /// <summary>
    /// The payload's <c>action</c> value. Null would mean "any action"; every entry here has one,
    /// because every event in this set carries a sub-action.
    /// </summary>
    public string? ActionValue { get; private set; }

    static partial void Initialize(WebhookEventType item)
    {
        (item.EventName, item.ActionValue) = item.Key switch
        {
            // Issues
            EWebhookEventType.IssuesOpened => ("issues", "opened"),
            EWebhookEventType.IssuesClosed => ("issues", "closed"),
            EWebhookEventType.IssuesReopened => ("issues", "reopened"),
            EWebhookEventType.IssuesLabeled => ("issues", "labeled"),
            EWebhookEventType.IssuesUnlabeled => ("issues", "unlabeled"),
            EWebhookEventType.IssuesAssigned => ("issues", "assigned"),
            EWebhookEventType.IssuesUnassigned => ("issues", "unassigned"),

            // Pull Requests. Closed and Merged deliberately share event+action; see the class remarks.
            EWebhookEventType.PullRequestOpened => ("pull_request", "opened"),
            EWebhookEventType.PullRequestClosed => ("pull_request", "closed"),
            EWebhookEventType.PullRequestMerged => ("pull_request", "closed"),
            EWebhookEventType.PullRequestReadyForReview => ("pull_request", "ready_for_review"),
            EWebhookEventType.PullRequestConvertedToDraft => ("pull_request", "converted_to_draft"),
            EWebhookEventType.PullRequestReviewRequested => ("pull_request", "review_requested"),

            // Pull Request Reviews. Approved and ChangesRequested share event+action; see the remarks.
            EWebhookEventType.PullRequestReviewApproved => ("pull_request_review", "submitted"),
            EWebhookEventType.PullRequestReviewChangesRequested => ("pull_request_review", "submitted"),
            EWebhookEventType.PullRequestReviewDismissed => ("pull_request_review", "dismissed"),

            // Check Runs
            EWebhookEventType.CheckRunCompleted => ("check_run", "completed"),

            // Issue Comments
            EWebhookEventType.IssueCommentCreated => ("issue_comment", "created"),

            _ => throw new System.ArgumentOutOfRangeException(nameof(item), item.Key, "An EWebhookEventType member without its GitHub event and action."),
        };
    }
}
