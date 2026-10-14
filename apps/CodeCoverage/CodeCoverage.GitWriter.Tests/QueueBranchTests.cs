using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// <c>POST /jobs/queue-branch</c> (docs/coverage_merge_queue_PRD.md §0 D65, D5, D52): the prepared commit rebuilt from the
/// prepare's own fields, checked against the id the prepare answered, and pushed to <c>mq/main/pr-1</c> under a lease.
/// The saga asks for it only right before a dispatch; nothing else writes to a queue branch.
/// </summary>
public class QueueBranchTests : WriterTestBase
{
    [Fact]
    public async Task The_prepared_commit_of_a_fork_head_is_rebuilt_and_pushed_to_the_queue_branch()
    {
        var origin = Seed();
        // A fork's pull request: the head exists only as refs/pull/1/head, which the forge hides from pushes, so the push
        // must send the pull request's blobs, which the blob-less clone fetches lazily.
        AddEntity(origin, "Order", "with-order");
        var head = origin.Commit("Add Order");
        origin.PullRequest(1, "feature");
        origin.Checkout("main").Write("README.md", "seed\nmain moved\n");
        var baseSha = origin.Commit("main: readme");
        origin.Push("main");
        var prepared = await Writer.PrepareAsync(PrepareRequest(origin, head, baseSha: baseSha));
        prepared.Outcome.Should().Be("prepared", prepared.Detail);

        var response = await Writer.PushQueueBranchAsync(QueueBranchRequest(origin, new Prepared(prepared.PreparedSha!, head, baseSha)));

        response.Outcome.Should().Be("pushed", response.Detail);
        response.Reason.Should().BeNull();
        response.PreparedSha.Should().Be(prepared.PreparedSha);
        var commit = prepared.PreparedSha!;
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(commit);
        // ONE commit on the base: the net change of the pull request plus main's own change, both dates the saga's.
        origin.InBare("log", "-1", "--format=%P|%an|%ae|%ad|%cn|%ce|%cd", "--date=raw", commit).Trim()
            .Should().Be($"{baseSha}|Pull Request Author|author@example.invalid|{AuthorDate.ToUnixTimeSeconds()} +0200|coverage-bot|bot@example.invalid|{CommitterDate.ToUnixTimeSeconds()} +0000");
        origin.InBare("diff-tree", "-r", "--name-status", baseSha, commit).Trim().Replace("\r", "")
            .Should().Be($"A\tapps/DemoApp/DemoApp/App_Data/Model/Order.json\nM\t{ModelHashes.Path}");
        origin.InBare("cat-file", "blob", $"{commit}:apps/DemoApp/DemoApp/App_Data/Model/Order.json").Should().Contain("Order");
        origin.InBare("fsck", "--no-dangling", "--strict");
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty("the job directory is deleted when the job ends");
    }

    [Fact]
    public async Task A_resolved_conflict_is_pushed_with_the_base_side_of_the_path()
    {
        var origin = Seed();
        var prepared = await PushedAsync(origin);

        var commit = origin.Ref($"refs/heads/{QueueBranch}")!;
        commit.Should().Be(prepared.Sha);
        origin.InBare("rev-parse", $"{commit}:{ModelHashes.Path}").Should().Be(origin.InBare("rev-parse", $"main:{ModelHashes.Path}"), "take-base");
        origin.InBare("log", "-1", "--format=%P", commit).Trim().Should().Be(prepared.Base);
        origin.InBare("cat-file", "blob", $"{commit}:{ModelHashes.Path}").Should().NotContain("<<<<<<<");
    }

    /// <summary>§0 D65: the push carries the commit the saga holds or nothing. A rebuild that differs (the request, the
    /// repository or the writer's git changed since the prepare) is refused, and the saga does not retry it.</summary>
    [Fact]
    public async Task A_rebuild_that_is_not_the_prepared_commit_is_refused_and_pushes_nothing()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);
        var other = prepared with { Sha = origin.InBare("rev-parse", "main").Trim() };

        var response = await Writer.PushQueueBranchAsync(QueueBranchRequest(origin, other));

        response.Outcome.Should().Be("prepared-differs");
        response.Detail.Should().Contain(prepared.Sha, "the detail names the commit the rebuild made");
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull();
    }

    /// <summary>The prepared commit sits on the base the prepare saw; a base branch that moved on since changes nothing
    /// (the saga restarts on a moved base itself, O20).</summary>
    [Fact]
    public async Task The_rebuild_uses_the_prepared_base_wherever_the_branch_is_now()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);
        origin.Checkout("main").Write("README.md", "seed\nmain moved on\n");
        origin.Commit("main: moved on");
        origin.Push("main");

        var response = await Writer.PushQueueBranchAsync(QueueBranchRequest(origin, prepared));

        response.Outcome.Should().Be("pushed", response.Detail);
        origin.InBare("log", "-1", "--format=%P", prepared.Sha).Trim().Should().Be(prepared.Base);
    }

    [Fact]
    public async Task A_head_that_moved_since_the_prepare_is_refused_and_pushes_nothing()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);
        origin.Checkout("feature").Write("README.md", "author pushed\n");
        origin.Commit("author: late push");
        origin.Push("feature").PullRequest(1, "feature");

        var response = await Writer.PushQueueBranchAsync(QueueBranchRequest(origin, prepared));

        response.Outcome.Should().Be("head-mismatch");
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull();
    }

    [Fact]
    public async Task The_queue_branch_is_written_under_a_lease()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);
        // Something that is not this job's commit holds the branch: the base itself.
        var foreign = origin.Ref("refs/heads/main")!;
        origin.SetBranch(QueueBranch, foreign);

        // "Must not exist" fails now that it exists; the right expectation overwrites it.
        var again = await Writer.PushQueueBranchAsync(QueueBranchRequest(origin, prepared));
        again.Outcome.Should().Be("queue-branch-moved");
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(foreign, "a rejected lease writes nothing");

        var leased = await Writer.PushQueueBranchAsync(QueueBranchRequest(origin, prepared, expectedQueueSha: foreign));
        leased.Outcome.Should().Be("pushed", leased.Detail);
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(prepared.Sha);
    }

    /// <summary>
    /// D51 (1): the push landed but the saga never saved it, so it asks again with the old lease. The branch holds this
    /// job's commit (the commit is a function of the request, §0 D65), which is the answer, instead of a reset of the
    /// branch and a second prepare.
    /// </summary>
    [Fact]
    public async Task A_retried_push_recognises_the_commit_an_earlier_attempt_pushed()
    {
        var origin = Seed();
        var prepared = await PushedAsync(origin);
        int Pushes() => Writer.Started.Count(p => p.ArgumentList.Contains("push"));
        var pushesBefore = Pushes();

        var again = await Writer.PushQueueBranchAsync(QueueBranchRequest(origin, prepared));

        again.Outcome.Should().Be("pushed", again.Detail);
        again.Reason.Should().Be("already-pushed");
        again.PreparedSha.Should().Be(prepared.Sha);
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(prepared.Sha);
        Pushes().Should().Be(pushesBefore + 1, "one push, which finds the branch already at the commit and sends nothing");
    }

    /// <summary>The control: a commit that differs from this job's in its committer date alone is someone else's now,
    /// since §0 D65 pins every date (before, D52 let a date git stamped itself differ).</summary>
    [Fact]
    public async Task A_commit_that_differs_only_in_its_date_is_not_adopted()
    {
        var origin = Seed();
        var prepared = await PushedAsync(origin);
        var redated = origin.Rewrite(prepared.Sha, CommitText.Redated);
        origin.SetBranch(QueueBranch, redated);

        var again = await Writer.PushQueueBranchAsync(QueueBranchRequest(origin, prepared));

        again.Outcome.Should().Be("queue-branch-moved");
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(redated);
    }
}
