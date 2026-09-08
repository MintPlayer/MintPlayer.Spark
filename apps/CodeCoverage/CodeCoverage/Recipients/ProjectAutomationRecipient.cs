using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using CodeCoverage.LookupReferences;
using CodeCoverage.Services;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Recipients;

/// <summary>
/// Moves project-board cards in response to repository webhook deliveries, according to each
/// board's configured rules.
/// </summary>
public partial class ProjectAutomationRecipient : IRecipient<ProjectAutomationMessage>
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IGitHubProjectCards cards;
    [Inject] private readonly ILogger<ProjectAutomationRecipient> logger;

    /// <summary>Bound on the boards one owner may have automating; the session's request budget is 30.</summary>
    private const int MaxBoardsPerOwner = 100;

    public async Task HandleAsync(ProjectAutomationMessage message, CancellationToken cancellationToken = default)
    {
        var slash = message.RepositoryFullName.IndexOf('/');
        if (slash <= 0)
        {
            // Not retryable: the payload will never gain a repository name. NonRetryableException
            // dead-letters on the first attempt rather than spending five retries and an hour of
            // backoff on a message that cannot improve.
            throw new NonRetryableException(
                $"Delivery {message.DeliveryId} has no owner/name repository ('{message.RepositoryFullName}').");
        }

        var owner = message.RepositoryFullName[..slash];
        var repo = message.RepositoryFullName[(slash + 1)..];

        var matched = ResolveEventKeys(message);
        if (matched.Count == 0)
        {
            logger.LogDebug("Delivery {DeliveryId} ({EventType}) matches no automatable rule key",
                message.DeliveryId, message.EventType);
            return;
        }

        var boards = await session.Query<GitHubProject, GitHubProjects_Overview>()
            .Where(p => p.OwnerLogin == owner
                        && p.AutomationEnabled
                        && p.Connection != RepositoryConnection.Disconnected)
            .Take(MaxBoardsPerOwner)
            .ToListAsync(cancellationToken);

        if (boards.Count == 0)
            return;

        var anyApplied = false;
        var failures = new List<string>();

        foreach (var board in boards)
        {
            foreach (var key in matched)
            {
                var rule = board.EventMappings.FirstOrDefault(m => m.Enabled && m.EventType == key.ToString());
                if (rule is null)
                    continue;

                try
                {
                    await ApplyAsync(board, rule, key, message, owner, repo, cancellationToken);
                    anyApplied = true;
                }
                catch (Exception ex)
                {
                    // Recorded on the RULE, not left to the message queue, and the distinction is
                    // deliberate. One delivery can match several boards, so throwing here would
                    // fail the whole message and its retry would re-run the moves that already
                    // succeeded — moving those cards twice, on a queue whose entire purpose is
                    // ordered, once-only handling. Completing the message is correct, which means
                    // the queue records nothing and the rule has to.
                    //
                    // A dead-lettered SparkMessage is also invisible to the board's configuration
                    // screen and is deleted after the retention window; the rule is what the user
                    // looks at.
                    logger.LogError(ex, "Rule {EventType} on board #{Number} ({Name}) failed",
                        key, board.Number, board.Name);
                    rule.LastError = ex.Message;
                    rule.LastErrorAtUtc = DateTime.UtcNow;
                    failures.Add($"#{board.Number}/{key}: {ex.Message}");
                }
            }
        }

        // Persist the rule outcomes — LastFiredAtUtc and LastError — before deciding the message's
        // fate, so the record survives even the throw below.
        await session.SaveChangesAsync(cancellationToken);

        // Only when NOTHING worked is the delivery itself a failure. Then the queue is the right
        // place to record it, and NonRetryableException is the right shape: a rule pointing at a
        // deleted column will not start working within five retries.
        if (!anyApplied && failures.Count > 0)
        {
            throw new NonRetryableException(
                $"Every matching rule failed for delivery {message.DeliveryId}: {string.Join("; ", failures)}");
        }
    }

    private async Task ApplyAsync(
        GitHubProject board,
        EventColumnMapping rule,
        EWebhookEventType key,
        ProjectAutomationMessage message,
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        // Checked before calling GitHub, so the error names the real problem. A rule stores an
        // option ID, and nothing tells this app when a column is deleted — the Projects V2 webhook
        // events are organization-scoped and unsubscribed either way — so this is the single most
        // likely way a working rule stops working.
        if (board.Columns.All(c => c.Id != rule.TargetColumnOptionId))
        {
            throw new InvalidOperationException(
                $"Target column '{rule.TargetColumnOptionId}' no longer exists on this board. "
                + "It was probably deleted or the board's Status field changed; run Sync columns and re-pick it.");
        }

        using var document = JsonDocument.Parse(message.EventJson);
        var root = document.RootElement;

        var outcome = key switch
        {
            // Issue events act on the issue's own card.
            EWebhookEventType.IssuesOpened
                or EWebhookEventType.IssuesClosed
                or EWebhookEventType.IssuesReopened
                or EWebhookEventType.IssuesLabeled
                or EWebhookEventType.IssuesUnlabeled
                or EWebhookEventType.IssuesAssigned
                or EWebhookEventType.IssuesUnassigned
                => await MoveIssueAsync(board, root, "issue", owner, repo, rule, cancellationToken),

            // A comment arrives on an issue OR a pull request; GitHub reports both under
            // issue_comment, with a pull_request member present only for the latter. The card is
            // the same item either way, so the issue path serves both.
            EWebhookEventType.IssueCommentCreated
                => await MoveIssueAsync(board, root, "issue", owner, repo, rule, cancellationToken),

            // Pull-request events act on the PR's own card, and — when the rule says so — on the
            // cards of the issues the PR closes. That second half used to belong to `Merged` alone,
            // which made every other pull-request rule inert on a board that tracks issues rather
            // than PRs: "ready for review" moved a PR card that was never there.
            EWebhookEventType.PullRequestOpened
                or EWebhookEventType.PullRequestClosed
                or EWebhookEventType.PullRequestMerged
                or EWebhookEventType.PullRequestReadyForReview
                or EWebhookEventType.PullRequestConvertedToDraft
                or EWebhookEventType.PullRequestReviewRequested
                => await MovePullRequestAsync(board, root, owner, repo, rule, cancellationToken),

            // Review events act on the reviewed PR's card, and on its linked issues on the same
            // terms — the payload carries the same `pull_request` member.
            EWebhookEventType.PullRequestReviewApproved
                or EWebhookEventType.PullRequestReviewChangesRequested
                or EWebhookEventType.PullRequestReviewDismissed
                => await MovePullRequestAsync(board, root, owner, repo, rule, cancellationToken),

            // A check run is attached to commits, and reaches a card only through the pull requests
            // those commits belong to.
            EWebhookEventType.CheckRunCompleted
                => await MoveCheckRunPullRequestsAsync(board, root, owner, repo, rule, cancellationToken),

            _ => ECardOutcome.NotOnBoard,
        };

        rule.LastFiredAtUtc = DateTime.UtcNow;
        rule.LastError = null;
        rule.LastErrorAtUtc = null;

        logger.LogInformation("Rule {EventType} on board #{Number} ({Name}): {Outcome}",
            key, board.Number, board.Name, outcome);
    }

    private async Task<ECardOutcome> MoveIssueAsync(
        GitHubProject board, JsonElement root, string member, string owner, string repo,
        EventColumnMapping rule, CancellationToken cancellationToken)
    {
        var number = ReadNumber(root, member)
            ?? throw new NonRetryableException($"No {member}.number in the payload.");

        // Board membership is the rule's decision, not this method's — see AutoAddToBoard. It
        // defaults to true, which is what makes `IssuesOpened` work at all: that event fires on an
        // issue which by definition was not on the board a moment ago.
        return await cards.MoveIssueAsync(board, owner, repo, number, rule.TargetColumnOptionId, rule.AutoAddToBoard, cancellationToken);
    }

    private async Task<ECardOutcome> MovePullRequestAsync(
        GitHubProject board, JsonElement root, string owner, string repo,
        EventColumnMapping rule, CancellationToken cancellationToken)
    {
        var number = ReadNumber(root, "pull_request")
            ?? throw new NonRetryableException("No pull_request.number in the payload.");

        // Whether the PR itself lands on the board is `AutoAddToBoard`, exactly as it is for an
        // issue — one flag, one meaning, every call site. This was briefly hard-coded to false on
        // the reasoning that a board tracking issues would drown in its own pull requests. True as
        // far as it goes, but it made the choice for the user and could not be turned off: combined
        // with the linked-issue pass below it left every non-merged PR rule with nothing to act on,
        // silently, reporting success. It is a default now, not an invariant.
        var outcome = await cards.MovePullRequestAsync(board, owner, repo, number, rule.TargetColumnOptionId, rule.AutoAddToBoard, cancellationToken);

        return await MoveLinkedIssuesAsync(board, owner, repo, number, rule, outcome, cancellationToken);
    }

    /// <summary>
    /// Moves the cards of the issues the pull request closes, when the rule asks for it — in
    /// addition to the pull request's own card, which the caller has already moved.
    /// </summary>
    /// <remarks>
    /// The outcome reported for the rule stays the pull request's own, because that is the item the
    /// event was about; a linked-issue move is a consequence, not the result. A linked issue that
    /// is missing and not being recruited is skipped silently rather than failing the rule — the
    /// PR referencing an issue nobody put on this board is normal, not an error.
    /// </remarks>
    private async Task<ECardOutcome> MoveLinkedIssuesAsync(
        GitHubProject board, string owner, string repo, int number,
        EventColumnMapping rule, ECardOutcome outcome, CancellationToken cancellationToken)
    {
        if (!rule.MoveLinkedIssues) return outcome;

        var closing = await cards.GetClosingIssuesAsync(board.InstallationId, owner, repo, number, cancellationToken);
        foreach (var (issueRepo, issueNumber) in closing)
        {
            await cards.MoveIssueAsync(
                board, owner, issueRepo, issueNumber, rule.TargetColumnOptionId,
                rule.AutoAddToBoard, cancellationToken);
        }

        return outcome;
    }

    private async Task<ECardOutcome> MoveCheckRunPullRequestsAsync(
        GitHubProject board, JsonElement root, string owner, string repo,
        EventColumnMapping rule, CancellationToken cancellationToken)
    {
        // A check run carries the pull requests its head commit belongs to. Note that GitHub
        // populates this only for PRs in the same repository — a fork's PR arrives with an empty
        // array — so a check_run rule legitimately does nothing for fork contributions.
        if (!root.TryGetProperty("check_run", out var checkRun)
            || !checkRun.TryGetProperty("pull_requests", out var pullRequests)
            || pullRequests.ValueKind != JsonValueKind.Array)
        {
            return ECardOutcome.NotOnBoard;
        }

        var outcome = ECardOutcome.NotOnBoard;
        foreach (var pullRequest in pullRequests.EnumerateArray())
        {
            if (!pullRequest.TryGetProperty("number", out var numberElement)
                || !numberElement.TryGetInt32(out var number))
            {
                continue;
            }

            outcome = await cards.MovePullRequestAsync(
                board, owner, repo, number, rule.TargetColumnOptionId, rule.AutoAddToBoard, cancellationToken);

            // Same terms as a direct PR event: the check run is about the PR, and on a board that
            // tracks issues the linked issue is what the rule is actually for.
            outcome = await MoveLinkedIssuesAsync(board, owner, repo, number, rule, outcome, cancellationToken);
        }

        return outcome;
    }

    /// <summary>
    /// Which rule keys this delivery matches.
    /// <para>
    /// A <b>list</b>, not one key, and matching is not a lookup on (event, action). Two pairs of
    /// keys share an event and action and are separated only by the payload:
    /// <c>pull_request.closed</c> is either <c>PullRequestClosed</c> or <c>PullRequestMerged</c>
    /// depending on the <c>merged</c> flag, and <c>pull_request_review.submitted</c> is either
    /// <c>PullRequestReviewApproved</c> or <c>PullRequestReviewChangesRequested</c> depending on the
    /// review's state. A resolver keyed on (event, action) alone maps a merge onto the "closed"
    /// rule and silently drops the "merged" one — which is how a rule can look configured and never
    /// fire.
    /// </para>
    /// </summary>
    internal static List<EWebhookEventType> ResolveEventKeys(ProjectAutomationMessage message)
    {
        var keys = new List<EWebhookEventType>();

        try
        {
            using var document = JsonDocument.Parse(message.EventJson);
            var root = document.RootElement;
            var action = root.TryGetProperty("action", out var actionElement) && actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString()
                : null;

            if (action is null)
                return keys;

            foreach (var candidate in WebhookEventType.Items)
            {
                if (candidate.EventName != message.EventType || candidate.ActionValue != action)
                    continue;

                // Disambiguate the two colliding pairs on payload state.
                var include = candidate.Key switch
                {
                    EWebhookEventType.PullRequestMerged => IsMerged(root),
                    EWebhookEventType.PullRequestClosed => !IsMerged(root),
                    EWebhookEventType.PullRequestReviewApproved => ReviewStateIs(root, "approved"),
                    EWebhookEventType.PullRequestReviewChangesRequested => ReviewStateIs(root, "changes_requested"),
                    _ => true,
                };

                if (include)
                    keys.Add(candidate.Key);
            }
        }
        catch (JsonException)
        {
            // Handled by the caller as "no rule matched": the signature proved GitHub sent this, so
            // an unparseable body is a shape we do not model rather than an attack.
        }

        return keys;
    }

    private static bool IsMerged(JsonElement root)
        => root.TryGetProperty("pull_request", out var pullRequest)
            && pullRequest.TryGetProperty("merged", out var merged)
            && merged.ValueKind == JsonValueKind.True;

    private static bool ReviewStateIs(JsonElement root, string state)
        => root.TryGetProperty("review", out var review)
            && review.TryGetProperty("state", out var reviewState)
            && reviewState.ValueKind == JsonValueKind.String
            && string.Equals(reviewState.GetString(), state, StringComparison.OrdinalIgnoreCase);

    private static int? ReadNumber(JsonElement root, string member)
        => root.TryGetProperty(member, out var element)
            && element.TryGetProperty("number", out var number)
            && number.TryGetInt32(out var value)
                ? value
                : null;
}
