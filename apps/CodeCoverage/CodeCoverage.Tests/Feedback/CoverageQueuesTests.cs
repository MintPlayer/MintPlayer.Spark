using System.Reflection;
using CodeCoverage.Feedback;
using MintPlayer.Spark.Messaging.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// A guard for the constraint that cost the most to discover: RavenDB caps data
/// subscriptions per database, Spark creates one subscription per distinct queue
/// name, and exceeding the cap kills a queue <b>silently</b> — the subscription
/// is never created, the worker dies as "non-recoverable", and the application
/// carries on looking perfectly healthy.
/// <para>
/// This app had five queues against a limit of three (one of which the framework
/// takes for webhooks), so three were dead. Merged-PR build deletion had never
/// run in production, unnoticed, for exactly this reason.
/// </para>
/// <para>
/// It is a compile-time-visible property of the code, so it should be asserted
/// at build time rather than discovered from a container log.
/// </para>
/// </summary>
public class CoverageQueuesTests
{
    /// <summary>
    /// The AGPL/open-source licence this deployment runs on allows three
    /// subscriptions per database, verified against the live server (a create
    /// beyond it answers 402 with LicenseLimitException). One is the framework's
    /// own webhook queue, so the application may declare two.
    /// </summary>
    private const int QueuesAvailableToThisApplication = 2;

    private static IReadOnlyList<string> DeclaredQueueNames()
    {
        // Every message type in the app assembly that names a queue.
        var messages = typeof(PublishFeedbackMessage).Assembly
            .GetTypes()
            .Select(t => t.GetCustomAttribute<MessageQueueAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.QueueName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return messages;
    }

    [Fact]
    public void The_application_declares_no_more_queues_than_its_licence_allows()
    {
        var queues = DeclaredQueueNames();

        queues.Count.Should().BeLessThanOrEqualTo(QueuesAvailableToThisApplication,
            $"each distinct queue costs one RavenDB data subscription and the cap is 3 (one taken by the " +
            $"framework's webhook queue); declared: {string.Join(", ", queues)}");
    }

    /// <summary>
    /// Names, not just the count: reusing the two that already exist on the
    /// server is what avoids asking for a new subscription at all. Renaming
    /// either one would require creating a subscription, which is the very thing
    /// the cap forbids.
    /// </summary>
    [Fact]
    public void The_declared_queues_are_exactly_the_two_that_already_exist_on_the_server()
        => DeclaredQueueNames().Should().BeEquivalentTo([CoverageQueues.Ingestion, CoverageQueues.Publishing]);

    /// <summary>
    /// Ingestion is strict FIFO and latency-sensitive; everything on the
    /// publishing queue makes GitHub API calls. Keeping them apart is the reason
    /// the app spends its second queue rather than putting everything on one.
    /// </summary>
    [Fact]
    public void Ingestion_and_publishing_are_kept_apart()
        => CoverageQueues.Ingestion.Should().NotBe(CoverageQueues.Publishing);
}
