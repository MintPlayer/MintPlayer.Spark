using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;

namespace WebhooksDemo.Recipients;

/// <summary>
/// Catch-all recipient that logs every GitHub webhook event.
/// </summary>
public partial class LogAllWebhooks : IRecipient<GitHubWebhookMessage>
{
    [Inject] private readonly ILogger<LogAllWebhooks> _logger;

    public Task HandleAsync(GitHubWebhookMessage message, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Webhook received: {EventType} from {Repo} (installation {InstallationId})",
            message.EventType, message.RepositoryFullName, message.InstallationId);
        return Task.CompletedTask;
    }
}
