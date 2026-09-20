using CodeCoverage.Forge;
using MintPlayer.Spark.Messaging.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.Forge;

/// <summary>
/// D21's contract: one recipient, any forge, and adding a forge edits no consumer.
/// </summary>
/// <remarks>
/// The property under test is the reason the neutral envelope exists, and it is provable **before**
/// GitLab or Bitbucket exist — a test double raising a second provider is indistinguishable, to a
/// recipient, from a real one. Waiting for a real second forge to find out would be finding out too
/// late, since the whole point is that the second forge costs no consumer changes.
/// </remarks>
public class ForgeWebhookMessageTests
{
    /// <summary>
    /// A consumer written once, naming no forge. If this ever needs a second method or a
    /// <c>switch</c> on <c>Provider</c> to handle a second forge, D21 has been lost.
    /// </summary>
    private sealed class RecordingRecipient
    {
        public List<(EForgeProvider Provider, string Sha)> Handled { get; } = [];

        public void Handle(ForgeWebhookMessage<BranchCommitPushed> message)
            => Handled.Add((message.Provider, message.Event.Sha));
    }

    private static ForgeWebhookMessage<BranchCommitPushed> Push(EForgeProvider provider, string sha)
        => new(provider, new BranchCommitPushed(
            RepositoryId: "Repositories/1",
            Branch: "main",
            Sha: sha,
            Message: "a commit",
            AuthoredAt: DateTimeOffset.UnixEpoch));

    /// <summary>The M×N property, stated as a test.</summary>
    [Fact]
    public void One_recipient_handles_every_forge_with_no_second_method()
    {
        var recipient = new RecordingRecipient();

        // Three forges, one handler method, zero consumer edits between them.
        recipient.Handle(Push(EForgeProvider.GitHub, "aaa"));
        recipient.Handle(Push(EForgeProvider.GitLab, "bbb"));
        recipient.Handle(Push(EForgeProvider.Bitbucket, "ccc"));

        recipient.Handled.Should().BeEquivalentTo(
        [
            (EForgeProvider.GitHub, "aaa"),
            (EForgeProvider.GitLab, "bbb"),
            (EForgeProvider.Bitbucket, "ccc"),
        ]);
    }

    /// <summary>
    /// The forge travels as <em>data</em>, not as a type distinction — which is what makes exact-type
    /// dispatch work. If these were per-forge types the bus would need one registration each, and a
    /// recipient of a shared base would silently receive nothing.
    /// </summary>
    [Fact]
    public void The_forge_is_a_field_so_every_forge_shares_one_message_type()
        => Push(EForgeProvider.GitLab, "x").GetType()
            .Should().Be(Push(EForgeProvider.GitHub, "x").GetType());

    /// <summary>
    /// ⚠️ The queue name must stay pinned. Without the attribute it derives from the CLR type, and
    /// for a constructed generic that embeds the type argument's <b>assembly-qualified</b> name — so
    /// every closed generic becomes its own queue and the name changes with an assembly version.
    /// That produced seven queue definitions, six of them orphans, in a real database.
    /// <c>[MessageQueue]</c> is <c>Inherited = false</c>, so nothing upstream can supply it.
    /// </summary>
    [Fact]
    public void Every_closed_generic_shares_one_pinned_queue()
    {
        var push = typeof(ForgeWebhookMessage<BranchCommitPushed>)
            .GetCustomAttributes(typeof(MessageQueueAttribute), inherit: false);
        var merged = typeof(ForgeWebhookMessage<PullRequestMerged>)
            .GetCustomAttributes(typeof(MessageQueueAttribute), inherit: false);

        push.Should().ContainSingle("the envelope must carry its own [MessageQueue]");
        merged.Should().ContainSingle();

        ((MessageQueueAttribute)push[0]).QueueName.Should().Be("spark-forge-all");
        ((MessageQueueAttribute)merged[0]).QueueName.Should().Be(((MessageQueueAttribute)push[0]).QueueName);
    }

    /// <summary>
    /// A commit event deliberately carries no parent sha. GitHub's <c>before</c> is the previous ref
    /// tip, which is not the new commit's parent in three of six push shapes, and every forge's
    /// equivalent has the same defect — so the neutral event refuses to carry a value it cannot
    /// define, and the pull-request event remains the one writer of a parent.
    /// </summary>
    [Fact]
    public void A_pushed_commit_carries_no_parent_sha()
        => typeof(BranchCommitPushed).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(name => name.Contains("Parent", StringComparison.OrdinalIgnoreCase));
}
