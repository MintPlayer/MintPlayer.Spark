using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.Issues;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using WebhooksDemo.Entities;
using WebhooksDemo.LookupReferences;
using WebhooksDemo.Services;

namespace WebhooksDemo.Recipients;

public partial class HandleIssuesEvent : IRecipient<GitHubWebhookMessage<IssuesEvent>>
{
    [Inject] private readonly IAsyncDocumentSession _session;
    [Inject] private readonly IGitHubProjectService _projectService;
    [Inject] private readonly ILogger<HandleIssuesEvent> _logger;

    public async Task HandleAsync(GitHubWebhookMessage<IssuesEvent> message, CancellationToken cancellationToken = default)
    {
        var eventType = message.Event.Action switch
        {
            IssuesActionValue.Opened => nameof(EWebhookEventType.IssuesOpened),
            IssuesActionValue.Closed => nameof(EWebhookEventType.IssuesClosed),
            IssuesActionValue.Reopened => nameof(EWebhookEventType.IssuesReopened),
            IssuesActionValue.Labeled => nameof(EWebhookEventType.IssuesLabeled),
            IssuesActionValue.Unlabeled => nameof(EWebhookEventType.IssuesUnlabeled),
            IssuesActionValue.Assigned => nameof(EWebhookEventType.IssuesAssigned),
            IssuesActionValue.Unassigned => nameof(EWebhookEventType.IssuesUnassigned),
            _ => (string?)null,
        };

        if (eventType == null) return;

        var owner = message.Event.Repository!.Owner.Login;
        var repo = message.Event.Repository.Name;
        var issueNumber = (int)message.Event.Issue.Number;

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
                    var moved = await _projectService.MoveOrAddIssueToColumnAsync(
                        message.InstallationId, project, owner, repo, issueNumber, mapping.TargetColumnOptionId, mapping.AutoAddToProject);

                    if (moved)
                        _logger.LogInformation("Moved issue #{Number} to column {Column} on project {Project}",
                            issueNumber, mapping.TargetColumnOptionId, project.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to move issue on project {Project} for event {Event}",
                        project.Name, mapping.WebhookEvent);
                }
            }
        }
    }
}
