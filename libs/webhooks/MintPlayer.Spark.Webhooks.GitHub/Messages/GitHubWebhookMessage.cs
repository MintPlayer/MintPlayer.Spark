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
/// <para>
/// <b>Shares the catch-all's queue on purpose.</b> Without an explicit
/// <see cref="MessageQueueAttribute"/> the queue name is derived from the CLR type, and for a
/// constructed generic that name embeds its argument's assembly-qualified name — so every closed
/// generic became its own queue, and the name changed whenever Octokit's assembly version did.
/// Field evidence: one database accumulated <b>seven</b> <c>SparkMessaging-*</c> definitions of
/// which six were orphans of exactly this shape, including separate
/// <c>Version=2.0.0.0</c> and <c>Version=3.0.0.0</c> variants of the same event. Nothing ever
/// deleted them, and under the old one-subscription-per-queue model each one consumed a slot from a
/// budget of three.
/// </para>
/// <para>
/// Pinning the name here means one queue however many event types are handled. The cost is the
/// usual one: a queue is a FIFO lane, so typed handlers are serialised with the catch-all and with
/// each other. That is the right default for webhook fan-out, where handlers are short and
/// ordering per repository is often what you want anyway.
/// </para>
/// </summary>
[MessageQueue("spark-github-all")]
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
