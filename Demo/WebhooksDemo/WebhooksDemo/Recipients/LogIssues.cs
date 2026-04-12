using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.Issues;

namespace WebhooksDemo.Recipients;

public partial class LogIssues : IRecipient<GitHubWebhookMessage<IssuesEvent>>
{
    [Inject] private readonly ILogger<LogIssues> _logger;
    [Inject] private readonly IGitHubInstallationService _gitHubInstallationService;

    public async Task HandleAsync(GitHubWebhookMessage<IssuesEvent> message, CancellationToken cancellationToken)
    {
        var issue = message.Event.Issue;
        _logger.LogInformation(
            "Issue #{Number} ({Action}): {Title} in {Repo}",
            issue.Number, message.Event.Action, issue.Title, message.RepositoryFullName);

        if (message.Event.Action == IssuesActionValue.Opened)
        {
            var githubClient = await _gitHubInstallationService.CreateInstallationClientAsync(message.InstallationId);
            await githubClient.Issue.Comment.Create(
                message.Event.Repository!.Id, (int)issue.Number, "Thanks for creating this issue");
        }
    }
}
