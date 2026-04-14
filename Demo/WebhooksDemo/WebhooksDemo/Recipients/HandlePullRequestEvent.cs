using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.PullRequest;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using WebhooksDemo.Entities;
using WebhooksDemo.LookupReferences;
using WebhooksDemo.Services;

namespace WebhooksDemo.Recipients;

public partial class HandlePullRequestEvent : IRecipient<GitHubWebhookMessage<PullRequestEvent>>
{
    [Inject] private readonly IAsyncDocumentSession _session;
    [Inject] private readonly IGitHubProjectService _projectService;
    [Inject] private readonly ILogger<HandlePullRequestEvent> _logger;

    public async Task HandleAsync(GitHubWebhookMessage<PullRequestEvent> message, CancellationToken cancellationToken = default)
    {
        var eventType = message.Event.Action switch
        {
            PullRequestActionValue.Opened => nameof(EWebhookEventType.PullRequestOpened),
            PullRequestActionValue.Closed when message.Event.PullRequest.Merged == true => nameof(EWebhookEventType.PullRequestMerged),
            PullRequestActionValue.Closed => nameof(EWebhookEventType.PullRequestClosed),
            PullRequestActionValue.ReadyForReview => nameof(EWebhookEventType.PullRequestReadyForReview),
            PullRequestActionValue.ConvertedToDraft => nameof(EWebhookEventType.PullRequestConvertedToDraft),
            PullRequestActionValue.ReviewRequested => nameof(EWebhookEventType.PullRequestReviewRequested),
            _ => (string?)null,
        };

        if (eventType == null) return;

        var owner = message.Event.Repository!.Owner.Login;
        var repo = message.Event.Repository.Name;
        var prNumber = (int)message.Event.PullRequest.Number;

        var projects = await _session.Query<GitHubProject>().ToListAsync(cancellationToken);

        foreach (var project in projects)
        {
            foreach (var mapping in project.EventMappings)
            {
                if (string.IsNullOrEmpty(mapping.WebhookEvent) || string.IsNullOrEmpty(mapping.TargetColumnOptionId))
                    continue;
                if (mapping.WebhookEvent != eventType)
                    continue;

                try
                {
                    var moved = await _projectService.MoveOrAddPullRequestToColumnAsync(
                        message.InstallationId, project, owner, repo, prNumber, mapping.TargetColumnOptionId);

                    if (moved)
                        _logger.LogInformation("Moved PR #{Number} to column {Column} on project {Project}",
                            prNumber, mapping.TargetColumnOptionId, project.Name);

                    if (mapping.MoveLinkedIssues)
                    {
                        var linkedIssues = await _projectService.GetClosingIssuesAsync(
                            message.InstallationId, owner, repo, prNumber);

                        foreach (var (linkedRepo, linkedNumber) in linkedIssues)
                        {
                            var linkedMoved = await _projectService.MoveOrAddIssueToColumnAsync(
                                message.InstallationId, project, owner, linkedRepo, linkedNumber, mapping.TargetColumnOptionId);

                            if (linkedMoved)
                                _logger.LogInformation("Moved linked issue #{Number} to column {Column} on project {Project}",
                                    linkedNumber, mapping.TargetColumnOptionId, project.Name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to move PR on project {Project} for event {Event}",
                        project.Name, mapping.WebhookEvent);
                }
            }
        }
    }
}
