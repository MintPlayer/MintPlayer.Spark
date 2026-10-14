using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// <c>POST /jobs/publish</c> and <c>DELETE /jobs/queue-branch</c> (docs/coverage_merge_queue_PRD.md §5.1 step [3], D5,
/// T3, T4): the regenerate patch is validated before anything runs, applied as a tree edit without a worktree, and the
/// final commit replaces the pull request's branch under a lease on the head pinned at enqueue.
/// </summary>
public class PublishTests : WriterTestBase
{
    /// <summary>The regenerate step's output for the S1 pair, as <c>git diff</c> writes it: the roll-ups and the
    /// Order lines rewritten in the hash file the resolver took from the base.</summary>
    private static string RegeneratePatch(Origin origin, string prepared)
    {
        var regenerated = ModelHashes.Render("with-customer-and-order", [.. ModelHashes.BaseEntities, "Customer", "Order"]);
        origin.Checkout("regen", prepared).Write(ModelHashes.Path, regenerated);
        origin.Commit("regenerate");
        var patch = FixtureGit.Run(origin.Work, "diff", "--binary", prepared, "HEAD");
        origin.Checkout("main");
        return patch;
    }

    [Fact]
    public async Task A_valid_patch_publishes_one_squashed_commit_on_the_base_with_the_author_preserved()
    {
        var origin = Seed();
        var (prepared, head) = await PreparedAsync(origin);
        FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", QueueBranch);
        var patch = RegeneratePatch(origin, prepared);
        var baseSha = origin.Ref("refs/heads/main")!;

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, head, patch));

        response.Outcome.Should().Be("published", $"{response.Detail}");
        response.Paths.Should().Equal([ModelHashes.Path]);
        var final = response.FinalSha!;
        origin.Ref("refs/heads/feature").Should().Be(final);
        // One parent, the base: base + the pull request's net change + the regenerated file, as one commit.
        origin.InBare("log", "-1", "--format=%P|%an|%ae|%ad|%cn|%ce", "--date=raw", final).Trim()
            .Should().Be($"{baseSha}|Pull Request Author|author@example.invalid|{AuthorDate.ToUnixTimeSeconds()} +0200|coverage-bot|bot@example.invalid");
        origin.InBare("log", "-1", "--format=%B", final).Should().StartWith("Add Order (#1)\n\nCo-authored-by: Someone");
        origin.InBare("diff-tree", "-r", "--name-only", prepared, final).Trim().Should().Be(ModelHashes.Path);
        origin.InBare("rev-parse", $"{final}:{ModelHashes.Path}").Should().Be(FixtureGit.Run(origin.Work, "rev-parse", $"regen:{ModelHashes.Path}"));
        origin.InBare("diff-tree", "-r", "--name-only", baseSha, final).Trim().Replace("\r", "")
            .Should().Be($"apps/DemoApp/DemoApp/App_Data/Model/Order.json\n{ModelHashes.Path}");
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_patch_the_prepared_commit_itself_is_published()
    {
        var origin = Seed();
        var (prepared, head) = await PreparedAsync(origin);

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, head));

        response.Outcome.Should().Be("published", $"{response.Detail}");
        response.FinalSha.Should().Be(prepared);
        origin.Ref("refs/heads/feature").Should().Be(prepared);
    }

    [Fact]
    public async Task A_patch_outside_the_regenerated_paths_is_rejected_before_any_git_runs()
    {
        var origin = Seed();
        var (prepared, head) = await PreparedAsync(origin);
        var started = Writer.Started.Count;
        const string patch = "diff --git a/src/Program.cs b/src/Program.cs\n--- a/src/Program.cs\n+++ b/src/Program.cs\n@@ -1 +1 @@\n-a\n+b\n";

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, head, patch));

        response.Outcome.Should().Be("patch-rejected");
        response.Reason.Should().Be("outside-regenerated-paths");
        response.Paths.Should().Equal(["src/Program.cs"]);
        Writer.Started.Count.Should().Be(started, "T4: validated before anything is cloned");
        origin.Ref("refs/heads/feature").Should().Be(head);
    }

    [Theory]
    [InlineData("symlink", "diff --git a/apps/DemoApp/DemoApp/App_Data/Model/X.json b/apps/DemoApp/DemoApp/App_Data/Model/X.json\nnew file mode 120000\nindex 0000000..1234567\n--- /dev/null\n+++ b/apps/DemoApp/DemoApp/App_Data/Model/X.json\n@@ -0,0 +1 @@\n+/etc/passwd\n\\ No newline at end of file\n")]
    [InlineData("binary", "diff --git a/apps/DemoApp/DemoApp/App_Data/modelHashes.json b/apps/DemoApp/DemoApp/App_Data/modelHashes.json\nindex 1234567..89abcde 100644\nGIT binary patch\nliteral 1\nIcmZpY0000\n\nliteral 0\nHcmV?d00001\n\n")]
    [InlineData("mode-change", "diff --git a/apps/DemoApp/DemoApp/App_Data/modelHashes.json b/apps/DemoApp/DemoApp/App_Data/modelHashes.json\nold mode 100644\nnew mode 100755\n")]
    public async Task A_symlink_a_binary_hunk_or_a_mode_change_is_rejected(string reason, string patch)
    {
        var origin = Seed();
        var (prepared, head) = await PreparedAsync(origin);

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, head, patch));

        response.Outcome.Should().Be("patch-rejected");
        response.Reason.Should().Be(reason);
        origin.Ref("refs/heads/feature").Should().Be(head);
    }

    [Fact]
    public async Task An_author_push_during_the_run_is_a_rejected_lease_not_a_failure()
    {
        var origin = Seed();
        var (prepared, head) = await PreparedAsync(origin);
        // The author pushes to the branch. GitHub updates refs/pull/N/head asynchronously, so the writer's early check
        // still sees the pinned head and the lease on refs/heads/feature is what catches it.
        origin.Checkout("feature").Write("README.md", "author pushed\n");
        var pushed = origin.Commit("author: late push");
        origin.Push("feature");

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, head));

        response.Outcome.Should().Be("branch-moved");
        response.Detail.Should().Contain("lease");
        origin.Ref("refs/heads/feature").Should().Be(pushed, "the author's commit is never overwritten");
    }

    [Fact]
    public async Task A_head_that_already_moved_is_reported_before_anything_is_built()
    {
        var origin = Seed();
        var (prepared, head) = await PreparedAsync(origin);
        origin.Checkout("feature").Write("README.md", "author pushed\n");
        origin.Commit("author: late push");
        origin.Push("feature").PullRequest(1, "feature");
        int Pushes() => Writer.Started.Count(p => p.ArgumentList.Contains("push"));
        var pushesBefore = Pushes();

        var response = await Writer.PublishAsync(PublishRequest(origin, prepared, head));

        response.Outcome.Should().Be("branch-moved");
        Pushes().Should().Be(pushesBefore, "nothing is pushed once the head is known to have moved");    }

    // ── D51 (1): a publish whose answer never reached the saga ──────────────────────────────────────

    /// <summary>
    /// The publish landed, but the saga never saved it (the app died, or the response was lost), so it sends the same
    /// request again. What the branch holds is the earlier attempt's commit, which differs from the one this attempt
    /// builds only in its committer date. <paramref name="forgeCaughtUp"/>: whether GitHub already moved
    /// <c>refs/pull/N/head</c> to it, so the writer meets it at its early check, or only as the lease's rejection.
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_retried_publish_recognises_the_commit_an_earlier_attempt_pushed(bool withPatch, bool forgeCaughtUp)
    {
        var origin = Seed();
        var (prepared, head) = await PreparedAsync(origin);
        string? patch = null;
        if (withPatch)
        {
            FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", QueueBranch);
            patch = RegeneratePatch(origin, prepared);
        }
        var request = PublishRequest(origin, prepared, head, patch);
        var first = await Writer.PublishAsync(request);
        first.Outcome.Should().Be("published", first.Detail);
        var earlier = origin.Rewrite(first.FinalSha!, CommitText.Redated);
        origin.SetBranch("feature", earlier);
        if (forgeCaughtUp)
            origin.InBare("update-ref", "refs/pull/1/head", earlier);

        var again = await Writer.PublishAsync(request);

        again.Outcome.Should().Be("published", $"the branch holds the queue's own commit, not an author's push: {again.Detail}");
        again.Reason.Should().Be("already-pushed");
        again.FinalSha.Should().Be(earlier, "the commit already on the branch is the answer; a second one is never pushed over it");
        origin.Ref("refs/heads/feature").Should().Be(earlier);
    }

    /// <summary>
    /// The same for a fork's pull request (O3): the branch is in the fork, so the rejected lease's commit is fetched from
    /// the fork's URL into the blob-less clone of the base, which is not the clone's promisor remote.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retried_publish_to_a_forks_branch_recognises_the_commit_an_earlier_attempt_pushed(bool withPatch)
    {
        var origin = Seed();
        var (prepared, head) = await PreparedAsync(origin);
        var fork = Fork(origin, "feature");
        string? patch = null;
        if (withPatch)
        {
            FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", QueueBranch);
            patch = RegeneratePatch(origin, prepared);
        }
        var request = PublishRequest(origin, prepared, head, patch, pushUrl: fork.Url);
        var first = await Writer.PublishAsync(request);
        first.Outcome.Should().Be("published", first.Detail);
        fork.Ref("refs/heads/feature").Should().Be(first.FinalSha, "O3: the fork's branch is the one rewritten");
        var earlier = fork.Rewrite(first.FinalSha!, CommitText.Redated);
        fork.SetBranch("feature", earlier);

        var again = await Writer.PublishAsync(request);

        again.Outcome.Should().Be("published", again.Detail);
        again.Reason.Should().Be("already-pushed");
        again.FinalSha.Should().Be(earlier);
        fork.Ref("refs/heads/feature").Should().Be(earlier);
        fork.InBare("fsck", "--no-dangling", "--strict");
    }

    /// <summary>
    /// Each criterion on its own: a commit that has the final tree but another committer, the bot's committer but another
    /// tree, or both but another parent than the base is not the queue's, and stays an author push.
    /// </summary>
    [Theory]
    [InlineData("committer")]
    [InlineData("tree")]
    [InlineData("parent")]
    public async Task A_commit_that_misses_one_criterion_is_still_an_author_push(string differs)
    {
        var origin = Seed();
        var (prepared, head) = await PreparedAsync(origin);
        FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", QueueBranch);
        var request = PublishRequest(origin, prepared, head, RegeneratePatch(origin, prepared));
        var first = await Writer.PublishAsync(request);
        first.Outcome.Should().Be("published", first.Detail);
        var preparedTree = origin.InBare("rev-parse", $"{prepared}^{{tree}}").Trim();
        var foreign = origin.Rewrite(first.FinalSha!, raw => CommitText.Redated(differs switch
        {
            "committer" => CommitText.Header(raw, "committer", _ => "committer Pull Request Author <author@example.invalid> 1767225600 +0000"),
            "tree" => CommitText.Header(raw, "tree", _ => $"tree {preparedTree}"),
            _ => CommitText.Header(raw, "parent", _ => $"parent {head}"),
        }));
        origin.SetBranch("feature", foreign);
        origin.InBare("update-ref", "refs/pull/1/head", foreign);

        var again = await Writer.PublishAsync(request);

        again.Outcome.Should().Be("branch-moved");
        origin.Ref("refs/heads/feature").Should().Be(foreign, "the author's commit is never overwritten");
    }

    [Fact]
    public async Task A_queue_branch_that_is_not_at_the_prepared_commit_is_refused()
    {
        var origin = Seed();
        var (_, head) = await PreparedAsync(origin);
        var other = origin.InBare("rev-parse", "main").Trim();

        var response = await Writer.PublishAsync(PublishRequest(origin, other, head));

        response.Outcome.Should().Be("queue-branch-moved");
        origin.Ref("refs/heads/feature").Should().Be(head);
    }

    [Fact]
    public async Task The_queue_branch_is_deleted_under_an_optional_lease_and_deleting_it_again_is_harmless()
    {
        var origin = Seed();
        var (prepared, _) = await PreparedAsync(origin);
        object Delete(string? expectedSha) => new
        {
            provider = "github", repoId = "1001", cloneUrl = origin.Url.AbsoluteUri, credential = Credential(Token), queueBranch = QueueBranch, expectedSha,
        };

        var wrong = await Writer.DeleteQueueBranchAsync(Delete(origin.InBare("rev-parse", "main").Trim()));
        var deleted = await Writer.DeleteQueueBranchAsync(Delete(prepared));
        var again = await Writer.DeleteQueueBranchAsync(Delete(null));

        wrong.Outcome.Should().Be("queue-branch-moved");
        deleted.Outcome.Should().Be("deleted", $"{deleted.Detail}");
        again.Outcome.Should().Be("deleted", $"{again.Detail}");
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull();
    }
}
