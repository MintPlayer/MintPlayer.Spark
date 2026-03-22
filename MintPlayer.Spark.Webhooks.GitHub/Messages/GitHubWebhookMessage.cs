using MintPlayer.Spark.Messaging.Abstractions;
using Octokit.Webhooks;

namespace MintPlayer.Spark.Webhooks.GitHub.Messages;

/// <summary>
/// Catch-all webhook message broadcast for every GitHub webhook event.
/// Implement <see cref="IRecipient{GitHubWebhookMessage}"/> to handle all events generically.
/// </summary>
[MessageQueue("spark-github-all")]
public record GitHubWebhookMessage
{
    public required WebhookHeaders Headers { get; init; }
    public required long InstallationId { get; init; }
    public required string RepositoryFullName { get; init; }
    public required string EventType { get; init; }
    public required string EventJson { get; init; }
}

/// <summary>
/// Typed webhook message for a specific GitHub event type.
/// Implement <see cref="IRecipient{GitHubWebhookMessage}"/> with
/// <c>GitHubWebhookMessage&lt;PullRequestEvent&gt;</c> etc. to handle specific events.
/// </summary>
public record GitHubWebhookMessage<TEvent> where TEvent : WebhookEvent
{
    public required WebhookHeaders Headers { get; init; }
    public required long InstallationId { get; init; }
    public required string RepositoryFullName { get; init; }
    public required TEvent Event { get; init; }
}
