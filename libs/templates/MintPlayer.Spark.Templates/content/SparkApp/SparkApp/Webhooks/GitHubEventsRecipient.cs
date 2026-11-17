using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;

namespace SparkApp.Webhooks;

/// <summary>
/// A sample recipient: it logs every GitHub delivery. Every IRecipient in this project is registered by
/// AddRecipients(). Take a typed GitHubWebhookMessage&lt;PushEvent&gt; (Octokit.Webhooks.Events) for one event only.
/// </summary>
public partial class GitHubEventsRecipient : IRecipient<GitHubWebhookMessage>
{
    [Inject] private readonly ILogger<GitHubEventsRecipient> logger;

    public Task HandleAsync(GitHubWebhookMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("GitHub {Event} for {Repository}", message.EventType, message.RepositoryFullName);
        return Task.CompletedTask;
    }
}
