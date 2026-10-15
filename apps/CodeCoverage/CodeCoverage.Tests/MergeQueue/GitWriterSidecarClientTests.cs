using CodeCoverage.CommitGraph;
using CodeCoverage.MergeQueue;
using Xunit;

namespace CodeCoverage.Tests.MergeQueue;

/// <summary>The app's side of the git writer's wire (merge-queue PRD §0 D65): the queue-branch push and the publish carry
/// their prepare's fields at the top level, as the writer's request types extend the prepare's.</summary>
public class GitWriterSidecarClientTests
{
    private static readonly GitWriterPrepareRequest Prepare = new(
        "github", "910040", "https://github.com/acme/widget.git", new SidecarCredential("x-access-token", "job-token"), "master",
        "1111111111111111111111111111111111111111", "refs/pull/7/head", "3333333333333333333333333333333333333333",
        [new GitWriterAutoResolve("**/App_Data/modelHashes.json", AnyLine: true)],
        new GitWriterIdentity("author", "author@users.noreply.github.com", new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero)),
        new GitWriterIdentity("CodeCoverage merge queue", "merge-queue@noreply.coverage.mintplayer.com", new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero)),
        "Add widgets (#7)");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_queue_branch_push_is_one_flat_object_with_the_prepares_fields(bool headFromFork)
    {
        var body = GitWriterSidecarClient.Body(new GitWriterQueueBranchRequest(Prepare, "4444444444444444444444444444444444444444", "mq/master/pr-7", null, headFromFork));

        body.ContainsKey("prepare").Should().BeFalse();
        body["baseSha"]!.GetValue<string>().Should().Be("1111111111111111111111111111111111111111");
        body["headSha"]!.GetValue<string>().Should().Be("3333333333333333333333333333333333333333");
        body["author"]!["date"]!.GetValue<DateTimeOffset>().Should().Be(new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero));
        body["committer"]!["date"].Should().NotBeNull();
        body["credential"]!["password"]!.GetValue<string>().Should().Be("job-token");
        body["preparedSha"]!.GetValue<string>().Should().Be("4444444444444444444444444444444444444444");
        body["queueBranch"]!.GetValue<string>().Should().Be("mq/master/pr-7");
        body.ContainsKey("expectedQueueSha").Should().BeTrue("null means the branch must not exist, so it is sent");
        // O25, §0 D67: the writer requires it, and refuses a fork's .github change itself; nothing on the wire allows one.
        body["headFromFork"]!.GetValue<bool>().Should().Be(headFromFork);
        body.ContainsKey("allowWorkflowChanges").Should().BeFalse();
    }

    [Fact]
    public void A_publish_is_one_flat_object_with_the_prepares_fields()
    {
        var body = GitWriterSidecarClient.Body(new GitWriterPublishRequest(Prepare, "4444444444444444444444444444444444444444", null, "feature-7", null, []));

        body.ContainsKey("prepare").Should().BeFalse();
        body["headSha"]!.GetValue<string>().Should().Be("3333333333333333333333333333333333333333", "the publish's lease");
        body["message"]!.GetValue<string>().Should().Be("Add widgets (#7)");
        body["prBranch"]!.GetValue<string>().Should().Be("feature-7");
        body["preparedSha"]!.GetValue<string>().Should().Be("4444444444444444444444444444444444444444");
    }

    [Fact]
    public void A_prepare_is_sent_as_it_is()
    {
        var body = GitWriterSidecarClient.Body(Prepare);

        body["baseBranch"]!.GetValue<string>().Should().Be("master");
        body.ContainsKey("queueBranch").Should().BeFalse("§0 D65: the prepare pushes nothing");
    }
}
