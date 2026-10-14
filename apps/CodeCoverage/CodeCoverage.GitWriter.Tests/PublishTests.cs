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
