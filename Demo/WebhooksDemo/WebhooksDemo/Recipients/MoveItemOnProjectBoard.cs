using System.Text.Json;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using WebhooksDemo.Entities;
using WebhooksDemo.LookupReferences;
using WebhooksDemo.Services;

namespace WebhooksDemo.Recipients;

public partial class MoveItemOnProjectBoard : IRecipient<GitHubWebhookMessage>
{
    [Inject] private readonly IAsyncDocumentSession _session;
    [Inject] private readonly IGitHubProjectService _projectService;
    [Inject] private readonly ILogger<MoveItemOnProjectBoard> _logger;

    public async Task HandleAsync(GitHubWebhookMessage message, CancellationToken cancellationToken)
    {
        var matchingEventTypes = ResolveEventTypes(message.EventType, message.EventJson);
        if (matchingEventTypes.Count == 0) return;

        var isIssueEvent = message.EventType == "issues";
        var isPullRequestEvent = message.EventType is "pull_request" or "pull_request_review";

        var (owner, repo, number) = ExtractEventTarget(message.EventType, message.EventJson);
        if (number == 0) return;

        var projects = await _session.Query<GitHubProject>()
            .ToListAsync(cancellationToken);

        foreach (var project in projects)
        {
            foreach (var mapping in project.EventMappings)
            {
                if (string.IsNullOrEmpty(mapping.WebhookEvent) || string.IsNullOrEmpty(mapping.TargetColumnOptionId))
                    continue;
                if (!matchingEventTypes.Contains(mapping.WebhookEvent))
                    continue;

                try
                {
                    if (isIssueEvent)
                    {
                        var moved = await _projectService.MoveIssueToColumnAsync(
                            message.InstallationId, project, owner, repo, number, mapping.TargetColumnOptionId);

                        if (moved)
                            _logger.LogInformation("Moved issue #{Number} to column {Column} on project {Project}",
                                number, mapping.TargetColumnOptionId, project.Name);
                    }
                    else if (isPullRequestEvent)
                    {
                        var moved = await _projectService.MovePullRequestToColumnAsync(
                            message.InstallationId, project, owner, repo, number, mapping.TargetColumnOptionId);

                        if (moved)
                            _logger.LogInformation("Moved PR #{Number} to column {Column} on project {Project}",
                                number, mapping.TargetColumnOptionId, project.Name);

                        if (mapping.MoveLinkedIssues)
                        {
                            var linkedIssues = await _projectService.GetClosingIssuesAsync(message.InstallationId, owner, repo, number);
                            foreach (var (linkedRepo, linkedNumber) in linkedIssues)
                            {
                                var linkedMoved = await _projectService.MoveIssueToColumnAsync(
                                    message.InstallationId, project, owner, linkedRepo, linkedNumber, mapping.TargetColumnOptionId);

                                if (linkedMoved)
                                    _logger.LogInformation("Moved linked issue #{Number} to column {Column} on project {Project}",
                                        linkedNumber, mapping.TargetColumnOptionId, project.Name);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to move item on project {Project} for event {Event}",
                        project.Name, mapping.WebhookEvent);
                }
            }
        }
    }

    private static List<string> ResolveEventTypes(string eventType, string eventJson)
    {
        using var doc = JsonDocument.Parse(eventJson);
        var root = doc.RootElement;

        var action = root.TryGetProperty("action", out var actionProp)
            ? actionProp.GetString() ?? string.Empty
            : string.Empty;

        var results = new List<string>();

        foreach (var item in WebhookEventType.Items)
        {
            if (item.EventName != eventType)
                continue;
            if (item.ActionValue != null && item.ActionValue != action)
                continue;

            // Special case: PullRequestMerged vs PullRequestClosed
            // Both have action="closed", distinguish by merged flag
            if (item.Key == EWebhookEventType.PullRequestMerged)
            {
                var merged = root.TryGetProperty("pull_request", out var pr)
                    && pr.TryGetProperty("merged", out var m)
                    && m.GetBoolean();
                if (!merged) continue;
            }
            else if (item.Key == EWebhookEventType.PullRequestClosed)
            {
                var merged = root.TryGetProperty("pull_request", out var pr)
                    && pr.TryGetProperty("merged", out var m)
                    && m.GetBoolean();
                if (merged) continue; // Skip — this is a merge, not a plain close
            }

            // Special case: PR review submitted — distinguish approved vs changes_requested
            if (item.Key == EWebhookEventType.PullRequestReviewApproved)
            {
                var state = root.TryGetProperty("review", out var review)
                    ? review.TryGetProperty("state", out var s) ? s.GetString() : null
                    : null;
                if (state != "approved") continue;
            }
            else if (item.Key == EWebhookEventType.PullRequestReviewChangesRequested)
            {
                var state = root.TryGetProperty("review", out var review)
                    ? review.TryGetProperty("state", out var s) ? s.GetString() : null
                    : null;
                if (state != "changes_requested") continue;
            }

            results.Add(item.Key.ToString());
        }

        return results;
    }

    private static (string Owner, string Repo, int Number) ExtractEventTarget(string eventType, string eventJson)
    {
        using var doc = JsonDocument.Parse(eventJson);
        var root = doc.RootElement;

        // Extract repository owner and name
        var owner = string.Empty;
        var repo = string.Empty;
        if (root.TryGetProperty("repository", out var repoProp))
        {
            if (repoProp.TryGetProperty("owner", out var ownerProp) && ownerProp.TryGetProperty("login", out var loginProp))
                owner = loginProp.GetString() ?? string.Empty;
            if (repoProp.TryGetProperty("name", out var nameProp))
                repo = nameProp.GetString() ?? string.Empty;
        }

        // Extract the issue/PR number
        int number = 0;
        if (eventType == "issues" && root.TryGetProperty("issue", out var issue))
        {
            number = issue.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
        }
        else if (eventType is "pull_request" && root.TryGetProperty("pull_request", out var pr))
        {
            number = pr.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
        }
        else if (eventType is "pull_request_review" && root.TryGetProperty("pull_request", out var prReview))
        {
            number = prReview.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
        }
        else if (eventType == "check_run" && root.TryGetProperty("check_run", out var checkRun))
        {
            // Check runs are linked to PRs via pull_requests array
            if (checkRun.TryGetProperty("pull_requests", out var prs) && prs.GetArrayLength() > 0)
                number = prs[0].TryGetProperty("number", out var n) ? n.GetInt32() : 0;
        }
        else if (eventType == "issue_comment" && root.TryGetProperty("issue", out var commentIssue))
        {
            number = commentIssue.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
        }

        return (owner, repo, number);
    }
}
