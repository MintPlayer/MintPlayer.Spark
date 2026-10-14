using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// <c>POST /jobs/publish</c> and <c>DELETE /jobs/queue-branch</c> (docs/coverage_merge_queue_PRD.md §5.1 step [3], D5,
/// T3, T4, §0 D65): the regenerate patch is validated before anything runs, the prepared commit is rebuilt from the
/// request (never read from the queue branch), the patch is applied as a tree edit without a worktree, and the final
/// commit replaces the pull request's branch under a lease on the head pinned at enqueue.
/// </summary>
public class PublishTests : WriterTestBase
{
    /// <summary>The regenerate step's output for the S1 pair, as <c>git diff</c> writes it: the roll-ups and the
    /// Order lines rewritten in the hash file the resolver took from the base. The run that makes it builds the queue
    /// branch, so the prepared commit is there (§0 D65: a patch exists only after a dispatch).</summary>
    private static string RegeneratePatch(Origin origin, Prepared prepared)
    {
        FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", QueueBranch);
        var regenerated = ModelHashes.Render("with-customer-and-order", [.. ModelHashes.BaseEntities, "Customer", "Order"]);
        origin.Checkout("regen", prepared.Sha).Write(ModelHashes.Path, regenerated);
        origin.Commit("regenerate");
        var patch = FixtureGit.Run(origin.Work, "diff", "--binary", prepared.Sha, "HEAD");
        origin.Checkout("main");
        return patch;
    }

    [Fact]
    public async Task A_valid_patch_publishes_one_squashed_commit_on_the_base_with_the_author_preserved()
    {
        var origin = Seed();
        var prepared = await PushedAsync(origin);
        var patch = RegeneratePatch(origin, prepared);

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, patch));

        response.Outcome.Should().Be("published", $"{response.Detail}");
        response.Paths.Should().Equal([ModelHashes.Path]);
        var final = response.FinalSha!;
        origin.Ref("refs/heads/feature").Should().Be(final);
        // One parent, the base: base + the pull request's net change + the regenerated file, as one commit, every date
        // the saga's (§0 D65).
        origin.InBare("log", "-1", "--format=%P|%an|%ae|%ad|%cn|%ce|%cd", "--date=raw", final).Trim()
            .Should().Be($"{prepared.Base}|Pull Request Author|author@example.invalid|{AuthorDate.ToUnixTimeSeconds()} +0200|coverage-bot|bot@example.invalid|{CommitterDate.ToUnixTimeSeconds()} +0000");
        origin.InBare("log", "-1", "--format=%B", final).Should().StartWith("Add Order (#1)\n\nCo-authored-by: Someone");
        origin.InBare("diff-tree", "-r", "--name-only", prepared.Sha, final).Trim().Should().Be(ModelHashes.Path);
        origin.InBare("rev-parse", $"{final}:{ModelHashes.Path}").Should().Be(FixtureGit.Run(origin.Work, "rev-parse", $"regen:{ModelHashes.Path}"));
        origin.InBare("diff-tree", "-r", "--name-only", prepared.Base, final).Trim().Replace("\r", "")
            .Should().Be($"apps/DemoApp/DemoApp/App_Data/Model/Order.json\n{ModelHashes.Path}");
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty();
    }

    /// <summary>
    /// §0 D65: without a dispatch there is no queue branch, and the publish rebuilds the prepared commit from the request.
    /// Before, it cloned <c>mq/</c> to find it and answered <c>queue-branch-moved</c> ("does not exist").
    /// </summary>
    [Fact]
    public async Task Without_a_patch_the_prepared_commit_is_rebuilt_and_published_with_no_queue_branch()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared));

        response.Outcome.Should().Be("published", $"{response.Detail}");
        response.FinalSha.Should().Be(prepared.Sha);
        origin.Ref("refs/heads/feature").Should().Be(prepared.Sha);
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull("only a dispatch needs the queue branch");
    }

    /// <summary>
    /// §0 D65: a fork's pull request that needs no regeneration never writes to the base repository at all: the prepare
    /// pushes nothing and the publish pushes to the fork. Before, the prepare pushed the fork's code to <c>mq/</c> in the
    /// base repository, which starts its <c>push</c> workflows on that code with its secrets.
    /// </summary>
    [Fact]
    public async Task A_forks_pull_request_without_a_dispatch_never_writes_to_the_base_repository()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);
        var fork = Fork(origin, "feature");
        var refs = origin.InBare("for-each-ref", "--format=%(refname) %(objectname)");

        var prepare = await Writer.PrepareAsync(PrepareRequest(origin, head));
        prepare.Outcome.Should().Be("prepared", prepare.Detail);
        var publish = await Writer.PublishAsync(PublishRequest(origin, new Prepared(prepare.PreparedSha!, head, prepare.BaseSha!), pushUrl: fork.Url));

        publish.Outcome.Should().Be("published", publish.Detail);
        fork.Ref("refs/heads/feature").Should().Be(prepare.PreparedSha, "O3: the fork's branch is the one rewritten");
        origin.InBare("for-each-ref", "--format=%(refname) %(objectname)").Should().Be(refs, "no ref of the base repository moved or appeared");
        fork.InBare("fsck", "--no-dangling", "--strict");
    }

    /// <summary>
    /// O25, §0 D67: a fork's change under <c>.github/</c> that needs no regeneration is published to the fork's branch,
    /// and nothing of it reaches the base repository: the base's <c>pull_request</c> workflows then run on it without
    /// secrets, as they would without the queue. Before, the rebuild refused it (<c>evicted</c>, <c>touches-workflows</c>),
    /// as the prepare did.
    /// </summary>
    [Fact]
    public async Task A_forks_change_under_github_is_published_to_the_fork_and_never_to_the_base_repository()
    {
        var origin = Seed();
        // A fork's pull request: the head exists only as refs/pull/1/head, and in the fork.
        origin.Write(".github/workflows/ci.yml", "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo pull-request-code\n");
        var head = origin.Commit("pr: a workflow");
        origin.PullRequest(1, "feature");
        origin.Checkout("main").Write("README.md", "seed\nmain moved\n");
        var baseSha = origin.Commit("main: readme");
        origin.Push("main");
        var fork = Fork(origin, "feature");
        var refs = origin.InBare("for-each-ref", "--format=%(refname) %(objectname)");

        var prepare = await Writer.PrepareAsync(PrepareRequest(origin, head, baseSha: baseSha));
        prepare.Outcome.Should().Be("prepared", prepare.Detail);
        prepare.TouchesWorkflows.Should().BeTrue();
        var publish = await Writer.PublishAsync(PublishRequest(origin, new Prepared(prepare.PreparedSha!, head, prepare.BaseSha!), pushUrl: fork.Url));

        publish.Outcome.Should().Be("published", publish.Detail);
        fork.Ref("refs/heads/feature").Should().Be(prepare.PreparedSha, "O3: the fork's branch is the one rewritten");
        fork.InBare("cat-file", "blob", $"{prepare.PreparedSha}:.github/workflows/ci.yml").Should().Contain("pull-request-code");
        origin.InBare("for-each-ref", "--format=%(refname) %(objectname)").Should().Be(refs, "no ref of the base repository moved or appeared");
        fork.InBare("fsck", "--no-dangling", "--strict");
    }

    /// <summary>
    /// O25, §0 D67: the publish needs no fork rule of its own. Sent without the fork's push URL (a defect in the saga), it
    /// would push to the base repository, and its lease on the pinned head refuses that: a fork's head is on no branch
    /// there, so no branch is created and none moves.
    /// </summary>
    [Fact]
    public async Task A_forks_publish_sent_to_the_base_repository_creates_nothing_there()
    {
        var origin = Seed();
        // A fork's pull request: the head exists only as refs/pull/1/head.
        origin.Write(".github/workflows/ci.yml", "on: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo pull-request-code\n");
        var head = origin.Commit("pr: a workflow");
        origin.PullRequest(1, "feature");
        var refs = origin.InBare("for-each-ref", "--format=%(refname) %(objectname)");
        var prepare = await Writer.PrepareAsync(PrepareRequest(origin, head));
        prepare.Outcome.Should().Be("prepared", prepare.Detail);

        var publish = await Writer.PublishAsync(PublishRequest(origin, new Prepared(prepare.PreparedSha!, head, prepare.BaseSha!)));

        publish.Outcome.Should().Be("branch-moved", publish.Detail);
        origin.Ref("refs/heads/feature").Should().BeNull();
        origin.InBare("for-each-ref", "--format=%(refname) %(objectname)").Should().Be(refs, "no ref of the base repository moved or appeared");
    }

    /// <summary>
    /// §0 D57: a repository without a regenerate step has no <c>regeneratedPaths</c>, and its publishes carry no patch. The
    /// writer refused such a request with a 400, so every case-2 pull request of such a repository ended as <c>error</c>.
    /// </summary>
    [Fact]
    public async Task A_publish_without_a_patch_needs_no_regenerated_paths()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, regeneratedPaths: []));

        response.Outcome.Should().Be("published", $"{response.Detail}");
        response.FinalSha.Should().Be(prepared.Sha);
    }

    [Fact]
    public async Task A_patch_with_no_regenerated_paths_is_rejected_as_outside_them()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);
        var patch = $"diff --git a/{ModelHashes.Path} b/{ModelHashes.Path}\n--- a/{ModelHashes.Path}\n+++ b/{ModelHashes.Path}\n@@ -1 +1 @@\n-a\n+b\n";

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, patch, regeneratedPaths: []));

        response.Outcome.Should().Be("patch-rejected");
        response.Reason.Should().Be("outside-regenerated-paths");
        origin.Ref("refs/heads/feature").Should().Be(prepared.Head);
    }

    [Fact]
    public async Task A_patch_outside_the_regenerated_paths_is_rejected_before_any_git_runs()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);
        var started = Writer.Started.Count;
        const string patch = "diff --git a/src/Program.cs b/src/Program.cs\n--- a/src/Program.cs\n+++ b/src/Program.cs\n@@ -1 +1 @@\n-a\n+b\n";

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, patch));

        response.Outcome.Should().Be("patch-rejected");
        response.Reason.Should().Be("outside-regenerated-paths");
        response.Paths.Should().Equal(["src/Program.cs"]);
        Writer.Started.Count.Should().Be(started, "T4: validated before anything is cloned");
        origin.Ref("refs/heads/feature").Should().Be(prepared.Head);
    }

    [Theory]
    [InlineData("symlink", "diff --git a/apps/DemoApp/DemoApp/App_Data/Model/X.json b/apps/DemoApp/DemoApp/App_Data/Model/X.json\nnew file mode 120000\nindex 0000000..1234567\n--- /dev/null\n+++ b/apps/DemoApp/DemoApp/App_Data/Model/X.json\n@@ -0,0 +1 @@\n+/etc/passwd\n\\ No newline at end of file\n")]
    [InlineData("binary", "diff --git a/apps/DemoApp/DemoApp/App_Data/modelHashes.json b/apps/DemoApp/DemoApp/App_Data/modelHashes.json\nindex 1234567..89abcde 100644\nGIT binary patch\nliteral 1\nIcmZpY0000\n\nliteral 0\nHcmV?d00001\n\n")]
    [InlineData("mode-change", "diff --git a/apps/DemoApp/DemoApp/App_Data/modelHashes.json b/apps/DemoApp/DemoApp/App_Data/modelHashes.json\nold mode 100644\nnew mode 100755\n")]
    public async Task A_symlink_a_binary_hunk_or_a_mode_change_is_rejected(string reason, string patch)
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, patch));

        response.Outcome.Should().Be("patch-rejected");
        response.Reason.Should().Be(reason);
        origin.Ref("refs/heads/feature").Should().Be(prepared.Head);
    }

    [Fact]
    public async Task An_author_push_during_the_run_is_a_rejected_lease_not_a_failure()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);
        // The author pushes to the branch. GitHub updates refs/pull/N/head asynchronously, so the writer's early check
        // still sees the pinned head and the lease on refs/heads/feature is what catches it.
        origin.Checkout("feature").Write("README.md", "author pushed\n");
        var pushed = origin.Commit("author: late push");
        origin.Push("feature");

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared));

        response.Outcome.Should().Be("branch-moved");
        response.Detail.Should().Contain("lease");
        origin.Ref("refs/heads/feature").Should().Be(pushed, "the author's commit is never overwritten");
    }

    [Fact]
    public async Task A_head_that_already_moved_is_reported_before_anything_is_built()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);
        origin.Checkout("feature").Write("README.md", "author pushed\n");
        origin.Commit("author: late push");
        origin.Push("feature").PullRequest(1, "feature");
        int Count(string verb) => Writer.Started.Count(p => p.ArgumentList.Contains(verb));
        var pushesBefore = Count("push");
        var mergesBefore = Count("merge-tree");

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared));

        response.Outcome.Should().Be("branch-moved");
        Count("push").Should().Be(pushesBefore, "nothing is pushed once the head is known to have moved");
        Count("merge-tree").Should().Be(mergesBefore, "nothing is built from a head that moved");
    }

    /// <summary>§0 D65: the publish carries the commit the saga holds or nothing; a rebuild that differs is refused.</summary>
    [Fact]
    public async Task A_publish_whose_rebuild_is_not_the_prepared_commit_is_refused()
    {
        var origin = Seed();
        var prepared = await PreparedAsync(origin);
        var other = prepared with { Sha = origin.InBare("rev-parse", "main").Trim() };

        var response = await Writer.PublishAsync(PublishRequest(origin, other));

        response.Outcome.Should().Be("prepared-differs");
        response.Detail.Should().NotContain("§", "the detail reaches the eviction message, whose readers never saw the PRD (§0 D72)");
        origin.Ref("refs/heads/feature").Should().Be(prepared.Head);
    }

    // ── D51 (1): a publish whose answer never reached the saga ──────────────────────────────────────

    /// <summary>
    /// The publish landed, but the saga never saved it (the app died, or the response was lost), so it sends the same
    /// request again. The branch holds the earlier attempt's commit, which is exactly the one this attempt builds: every
    /// date is the saga's (§0 D65). <paramref name="forgeCaughtUp"/>: whether GitHub already moved
    /// <c>refs/pull/N/head</c> to it, so the writer meets it at its early check (and the pinned head that built it is no
    /// longer the pull request's), or only as the lease's rejection.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_retried_publish_recognises_the_commit_an_earlier_attempt_pushed(bool withPatch, bool forgeCaughtUp)
    {
        var origin = Seed();
        var prepared = withPatch ? await PushedAsync(origin) : await PreparedAsync(origin);
        var patch = withPatch ? RegeneratePatch(origin, prepared) : null;
        var request = PublishRequest(origin, prepared, patch);
        var first = await Writer.PublishAsync(request);
        first.Outcome.Should().Be("published", first.Detail);
        var earlier = first.FinalSha!;
        if (forgeCaughtUp)
            origin.InBare("update-ref", "refs/pull/1/head", earlier);

        var again = await Writer.PublishAsync(request);

        again.Outcome.Should().Be("published", $"the branch holds the queue's own commit, not an author's push: {again.Detail}");
        again.Reason.Should().Be("already-pushed");
        again.FinalSha.Should().Be(earlier, "the commit already on the branch is the answer; a second one is never pushed over it");
        origin.Ref("refs/heads/feature").Should().Be(earlier);
    }

    /// <summary>
    /// The same for a fork's pull request (O3): the branch is in the fork, so the rejected lease's tip is read from the
    /// fork's URL, which is not the clone's promisor remote.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_publish_to_a_forks_branch_recognises_the_commit_an_earlier_attempt_pushed(bool withPatch)
    {
        var origin = Seed();
        var prepared = withPatch ? await PushedAsync(origin) : await PreparedAsync(origin);
        var fork = Fork(origin, "feature");
        var patch = withPatch ? RegeneratePatch(origin, prepared) : null;
        var request = PublishRequest(origin, prepared, patch, pushUrl: fork.Url);
        var first = await Writer.PublishAsync(request);
        first.Outcome.Should().Be("published", first.Detail);
        fork.Ref("refs/heads/feature").Should().Be(first.FinalSha, "O3: the fork's branch is the one rewritten");

        var again = await Writer.PublishAsync(request);

        again.Outcome.Should().Be("published", again.Detail);
        again.Reason.Should().Be("already-pushed");
        again.FinalSha.Should().Be(first.FinalSha);
        fork.Ref("refs/heads/feature").Should().Be(first.FinalSha);
        fork.InBare("fsck", "--no-dangling", "--strict");
    }

    /// <summary>
    /// Each criterion on its own: a commit that has the final tree but another committer or another committer date, the
    /// bot's committer but another tree, or both but another parent than the base is not the queue's, and stays an author
    /// push. The date is a criterion since §0 D65: the saga pins it, so an earlier attempt wrote the very same one.
    /// </summary>
    [Theory]
    [InlineData("committer")]
    [InlineData("date")]
    [InlineData("tree")]
    [InlineData("parent")]
    public async Task A_commit_that_misses_one_criterion_is_still_an_author_push(string differs)
    {
        var origin = Seed();
        var prepared = await PushedAsync(origin);
        var request = PublishRequest(origin, prepared, RegeneratePatch(origin, prepared));
        var first = await Writer.PublishAsync(request);
        first.Outcome.Should().Be("published", first.Detail);
        var preparedTree = origin.InBare("rev-parse", $"{prepared.Sha}^{{tree}}").Trim();
        var foreign = origin.Rewrite(first.FinalSha!, raw => differs switch
        {
            "committer" => CommitText.Header(raw, "committer", line => $"committer Pull Request Author <author@example.invalid>{line[(line.LastIndexOf('>') + 1)..]}"),
            "date" => CommitText.Redated(raw),
            "tree" => CommitText.Header(raw, "tree", _ => $"tree {preparedTree}"),
            _ => CommitText.Header(raw, "parent", _ => $"parent {prepared.Head}"),
        });
        origin.SetBranch("feature", foreign);
        origin.InBare("update-ref", "refs/pull/1/head", foreign);

        var again = await Writer.PublishAsync(request);

        again.Outcome.Should().Be("branch-moved");
        origin.Ref("refs/heads/feature").Should().Be(foreign, "the author's commit is never overwritten");
    }

    [Fact]
    public async Task The_queue_branch_is_deleted_under_an_optional_lease_and_deleting_it_again_is_harmless()
    {
        var origin = Seed();
        var prepared = await PushedAsync(origin);
        object Delete(string? expectedSha) => new
        {
            provider = "github", repoId = "1001", cloneUrl = origin.Url.AbsoluteUri, credential = Credential(Token), queueBranch = QueueBranch, expectedSha,
        };

        var wrong = await Writer.DeleteQueueBranchAsync(Delete(origin.InBare("rev-parse", "main").Trim()));
        var deleted = await Writer.DeleteQueueBranchAsync(Delete(prepared.Sha));
        var again = await Writer.DeleteQueueBranchAsync(Delete(null));

        wrong.Outcome.Should().Be("queue-branch-moved");
        deleted.Outcome.Should().Be("deleted", $"{deleted.Detail}");
        again.Outcome.Should().Be("deleted", $"{again.Detail}");
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull();
    }
}
