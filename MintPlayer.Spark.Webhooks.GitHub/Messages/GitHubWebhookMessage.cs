using System.Text.Json;
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
/// The <see cref="Event"/> property lazily deserializes from <see cref="EventJson"/>
/// using System.Text.Json (which has Octokit's read converters).
/// </summary>
public record GitHubWebhookMessage<TEvent> where TEvent : WebhookEvent
{
    public required WebhookHeaders Headers { get; init; }
    public required long InstallationId { get; init; }
    public required string RepositoryFullName { get; init; }
    public required string EventJson { get; init; }

    /// <summary>Deserialized event — computed from <see cref="EventJson"/> on first access.</summary>
    [Newtonsoft.Json.JsonIgnore]
    [System.Text.Json.Serialization.JsonIgnore]
    public TEvent Event => JsonSerializer.Deserialize<TEvent>(EventJson)!;
}
