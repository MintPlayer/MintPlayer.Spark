using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks.Events;

namespace WebhooksDemo.Recipients;

public partial class LogIssues : IRecipient<GitHubWebhookMessage<IssuesEvent>>
{
    [Inject] private readonly ILogger<LogIssues> _logger;

    public Task HandleAsync(GitHubWebhookMessage<IssuesEvent> message, CancellationToken cancellationToken)
    {
        var issue = message.Event.Issue;
        _logger.LogInformation(
            "Issue #{Number} ({Action}): {Title} in {Repo}",
            issue.Number, message.Event.Action, issue.Title, message.RepositoryFullName);
        return Task.CompletedTask;
    }
}
