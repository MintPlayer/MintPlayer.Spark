using System.Reflection;
using CodeCoverage.Feedback;
using MintPlayer.Spark.Messaging.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.Feedback;

/// <summary>
/// What is left of the queue guards now that a queue name no longer costs a RavenDB data
/// subscription.
/// <para>
/// This class used to assert two things that are now <b>wrong to assert</b>, and they were deleted
/// rather than adapted: that the application declares no more than two queues, and that the two are
/// exactly the pair already present on the server. Both encoded the old design, where Spark created
/// one subscription per distinct queue name and RavenDB capped subscriptions per database at three
/// on this licence — so a third queue name silently killed a queue and renaming one required
/// creating a subscription the cap forbade. Messaging now runs a single shared subscription with
/// in-process per-queue lanes, so both facts would fail a legitimate change while protecting
/// nothing.
/// </para>
/// <para>
/// The history is worth stating once, because the cost was real: this app reached seven queues
/// against a limit of three, so five were dead. Verified against production 2026-09-06 — the only
/// subscriptions that existed were <c>SparkMessaging-coverage-parse-session</c>,
/// <c>SparkMessaging-coverage-publish-feedback</c> and <c>SparkMessaging-spark-github-all</c>.
/// Merged-PR build deletion had never run, the sticky PR comment never appeared, and "Delete data"
/// queued a message nothing would ever consume. The equivalent guard today is not a count but
/// <c>MessageSubscriptionManagerLifecycleTests</c>, which asserts that however many queues are
/// declared, exactly one subscription is created.
/// </para>
/// </summary>
public class CoverageQueuesTests
{
    private static IReadOnlyList<string> DeclaredQueueNames()
        => typeof(PublishFeedbackMessage).Assembly
            .GetTypes()
            .Select(t => t.GetCustomAttribute<MessageQueueAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.QueueName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Ingestion is strict FIFO and latency-sensitive; everything on the publishing queue makes
    /// GitHub API calls. Keeping them apart is now the <i>only</i> reason the app spends a second
    /// queue — one queue is one FIFO lane, so sharing would let a slow report parse delay a check-run
    /// publish. That trade-off, not the licence, is what decides whether to split a queue.
    /// </summary>
    [Fact]
    public void Ingestion_and_publishing_are_kept_apart()
        => CoverageQueues.Ingestion.Should().NotBe(CoverageQueues.Publishing);

    /// <summary>
    /// Every declared queue name must still be a valid one. This survives the rework for a
    /// different reason than the deleted facts: in <c>SubscriptionPerQueue</c> mode the name is
    /// interpolated into RQL, so an invalid name is a real failure rather than a style question.
    /// </summary>
    [Fact]
    public void Every_declared_queue_name_is_a_valid_identifier()
    {
        foreach (var name in DeclaredQueueNames())
        {
            name.Should().NotBeNullOrWhiteSpace();
            name.Should().MatchRegex("^[A-Za-z0-9._+`-]+$",
                "queue names are interpolated into RQL in SubscriptionPerQueue mode");
        }
    }
}
