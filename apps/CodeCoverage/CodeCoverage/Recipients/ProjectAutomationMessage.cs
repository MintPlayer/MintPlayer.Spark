using CodeCoverage.Feedback;
using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;

namespace CodeCoverage.Recipients;

/// <summary>
/// A GitHub webhook delivery, re-published onto the project-automation queue.
/// <para>
/// This exists because the queue is chosen by the <em>message type</em>, not by the recipient. Two
/// recipients of <see cref="GitHubWebhookMessage"/> both run on <c>spark-github-all</c>, however
/// they are registered — so putting board automation on its own lane means re-publishing the
/// delivery under a type that declares that lane.
/// </para>
/// <para>
/// The alternative was a second <c>IRecipient&lt;GitHubWebhookMessage&gt;</c> sharing the
/// catch-all queue. That was the original plan while the subscription cap made a new queue name
/// dangerous; with the cap gone, the cost of sharing is the thing that remains — one FIFO lane,
/// so a board reconciliation's GraphQL round trips would delay coverage feedback. One extra
/// document per delivery is a cheap price for that isolation, and it also gives board automation
/// its own retry and dead-letter state rather than sharing a budget with account sync.
/// </para>
/// </summary>
[MessageQueue(ProjectAutomationQueue.Name)]
public sealed record ProjectAutomationMessage
{
    /// <summary>The GitHub event name, e.g. <c>pull_request</c>.</summary>
    public required string EventType { get; init; }

    /// <summary>The installation the delivery arrived through; the token every board mutation runs under.</summary>
    public required long InstallationId { get; init; }

    /// <summary><c>owner/name</c> of the repository the event concerns, empty when the event carries none.</summary>
    public required string RepositoryFullName { get; init; }

    /// <summary>The raw event payload, deserialized per event type by the recipient.</summary>
    public required string EventJson { get; init; }

    /// <summary>
    /// GitHub's delivery id, carried through so the re-publish inherits the original delivery's
    /// identity. Without it a GitHub redelivery would deduplicate on the catch-all queue and then
    /// enqueue a second automation message anyway.
    /// </summary>
    public required string DeliveryId { get; init; }
}
