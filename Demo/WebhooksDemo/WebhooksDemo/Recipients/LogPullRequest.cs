using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks.Events;

namespace WebhooksDemo.Recipients;

public partial class LogPullRequest : IRecipient<GitHubWebhookMessage<PullRequestEvent>>
{
    [Inject] private readonly ILogger<LogPullRequest> _logger;

    public Task HandleAsync(GitHubWebhookMessage<PullRequestEvent> message, CancellationToken cancellationToken)
    {
        var pr = message.Event.PullRequest;
        _logger.LogInformation(
            "PR #{Number} ({Action}): {Title} in {Repo}",
            pr.Number, message.Event.Action, pr.Title, message.RepositoryFullName);
        return Task.CompletedTask;
    }
}
