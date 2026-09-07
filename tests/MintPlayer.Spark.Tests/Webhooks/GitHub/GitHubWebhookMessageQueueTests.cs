using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Webhooks.GitHub.Messages;
using Octokit.Webhooks.Events;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// The typed webhook envelope must not derive a queue name per closed generic.
/// <para>
/// This is the fact that would have caught a real production mess. Without an explicit
/// <c>[MessageQueue]</c>, a queue name is derived from the CLR type, and a constructed generic's
/// name embeds its argument's assembly-qualified name — so every event type became its own queue,
/// and the name changed whenever Octokit's assembly version did. One database accumulated seven
/// <c>SparkMessaging-*</c> subscription definitions of which six were orphans of exactly that
/// shape, including separate <c>Version=2.0.0.0</c> and <c>Version=3.0.0.0</c> variants of the same
/// event, and nothing ever deleted them.
/// </para>
/// </summary>
public class GitHubWebhookMessageQueueTests
{
    private static string QueueNameOf<T>()
        => (Attribute.GetCustomAttribute(typeof(T), typeof(MessageQueueAttribute)) as MessageQueueAttribute)
            ?.QueueName ?? typeof(T).FullName!;

    [Fact]
    public void The_catch_all_envelope_declares_the_shared_webhook_queue()
        => QueueNameOf<GitHubWebhookMessage>().Should().Be("spark-github-all");

    [Fact]
    public void Every_closed_generic_envelope_resolves_to_the_same_queue_as_the_catch_all()
    {
        var pullRequest = QueueNameOf<GitHubWebhookMessage<PullRequestEvent>>();
        var issues = QueueNameOf<GitHubWebhookMessage<IssuesEvent>>();
        var push = QueueNameOf<GitHubWebhookMessage<PushEvent>>();

        pullRequest.Should().Be("spark-github-all");
        issues.Should().Be("spark-github-all");
        push.Should().Be("spark-github-all");
    }

    /// <summary>
    /// The failure mode stated directly: a derived name for a constructed generic carries the
    /// argument's assembly identity, so it is neither stable across dependency upgrades nor shared
    /// between event types. Asserting what the derived name <i>would</i> look like keeps the reason
    /// for the attribute legible if anyone considers removing it.
    /// </summary>
    [Fact]
    public void A_derived_name_for_a_closed_generic_would_embed_the_argument_assembly()
    {
        var derived = typeof(GitHubWebhookMessage<PullRequestEvent>).FullName!;

        derived.Should().Contain("Octokit.Webhooks");
        derived.Should().Contain("Version=");
        derived.Should().NotBe(typeof(GitHubWebhookMessage<IssuesEvent>).FullName,
            "a per-closed-generic name gives every event type its own queue");
    }
}
