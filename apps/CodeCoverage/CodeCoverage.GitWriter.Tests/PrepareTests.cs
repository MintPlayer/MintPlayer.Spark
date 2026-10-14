using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// <c>POST /jobs/prepare</c> (docs/coverage_merge_queue_PRD.md §5.1 step [1], D5, D12, T3) against a local smart-HTTP
/// forge: a real blob-less clone, a real fetch of <c>refs/pull/1/head</c>, the shared resolver, <c>commit-tree</c> and a
/// leased push to <c>mq/main/pr-1</c>.
/// </summary>
public class PrepareTests : WriterTestBase
{
    [Fact]
    public async Task A_clean_merge_of_a_fork_head_is_squashed_onto_the_base_and_pushed_to_the_queue_branch()
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

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head, baseSha: baseSha));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.BaseSha.Should().Be(baseSha);
        response.HeadSha.Should().Be(head);
        response.ResolvedPaths.Should().BeEmpty();
        var prepared = response.PreparedSha!;
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(prepared);
        // ONE commit on the base: the net change of the pull request plus main's own change, author kept, bot committer.
        origin.InBare("log", "-1", "--format=%P|%an|%ae|%at|%cn|%ce", prepared).Trim()
            .Should().Be($"{baseSha}|Pull Request Author|author@example.invalid|{AuthorDate.ToUnixTimeSeconds()}|coverage-bot|bot@example.invalid");
        origin.InBare("diff-tree", "-r", "--name-status", baseSha, prepared).Trim().Replace("\r", "")
            .Should().Be($"A\tapps/DemoApp/DemoApp/App_Data/Model/Order.json\nM\t{ModelHashes.Path}");
        origin.InBare("cat-file", "blob", $"{prepared}:apps/DemoApp/DemoApp/App_Data/Model/Order.json").Should().Contain("Order");
        origin.InBare("fsck", "--no-dangling", "--strict");
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty("the job directory is deleted when the job ends");
    }

    [Fact]
    public async Task A_roll_up_conflict_is_resolved_to_the_base_side_and_the_path_is_reported()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);
        var baseSha = origin.Ref("refs/heads/main")!;

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.ResolvedPaths.Should().Equal([ModelHashes.Path]);
        var prepared = response.PreparedSha!;
        origin.InBare("rev-parse", $"{prepared}:{ModelHashes.Path}").Should().Be(origin.InBare("rev-parse", $"main:{ModelHashes.Path}"), "take-base");
        origin.InBare("log", "-1", "--format=%P", prepared).Trim().Should().Be(baseSha);
        origin.InBare("cat-file", "blob", $"{prepared}:{ModelHashes.Path}").Should().NotContain("<<<<<<<");
    }

    [Fact]
    public async Task A_conflict_in_a_hand_authored_file_evicts_and_pushes_nothing()
    {
        var origin = Seed();
        origin.Write(Translations, "{\n  \"Address\": { \"en\": \"Address (pr)\" }\n}\n");
        var head = origin.Commit("pr: translation");
        origin.Push("feature").PullRequest(1, "feature");
        origin.Checkout("main").Write(Translations, "{\n  \"Address\": { \"en\": \"Address (main)\" }\n}\n");
        origin.Commit("main: translation");
        origin.Push("main");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("evicted");
        response.Reason.Should().Be("conflict-outside-allow-list");
        response.Paths.Should().Equal([Translations]);
        response.PreparedSha.Should().BeNull();
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull();
    }

    [Fact]
    public async Task A_whole_file_entry_resolves_the_same_conflict()
    {
        var origin = Seed();
        origin.Write(Translations, "{\n  \"Address\": { \"en\": \"Address (pr)\" }\n}\n");
        var head = origin.Commit("pr: translation");
        origin.Push("feature").PullRequest(1, "feature");
        origin.Checkout("main").Write(Translations, "{\n  \"Address\": { \"en\": \"Address (main)\" }\n}\n");
        origin.Commit("main: translation");
        origin.Push("main");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head, autoResolve: new object[] { new { path = "**/translations.json", anyLine = true } }));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.ResolvedPaths.Should().Equal([Translations]);
    }

    [Theory]
    [InlineData(".github/workflows/regenerate.yml")]
    [InlineData(".GitHub/actions/x/action.yml")]
    public async Task A_change_under_github_evicts_even_without_a_conflict(string path)
    {
        var origin = Seed();
        origin.Write(path, "on: push\n");
        var head = origin.Commit("pr: touch workflows");
        origin.Push("feature").PullRequest(1, "feature");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("evicted");
        response.Reason.Should().Be("touches-workflows");
        response.Paths.Should().Equal([path]);
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull();
    }

    [Fact]
    public async Task A_head_that_is_not_the_pinned_sha_is_refused_before_anything_is_merged()
    {
        var origin = Seed();
        var pinned = origin.Commit("pr: approved");
        origin.Write("README.md", "pushed after approval\n");
        var later = origin.Commit("pr: after approval");
        origin.Push("feature").PullRequest(1, "feature");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, pinned));

        response.Outcome.Should().Be("head-mismatch");
        response.HeadSha.Should().Be(later);
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull();
        Writer.Started.Should().NotContain(p => p.ArgumentList.Contains("merge-tree"), "T3: nothing is merged from an unpinned head");
    }

    [Fact]
    public async Task A_base_that_moved_since_the_saga_looked_is_reported()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);
        var stale = origin.InBare("rev-parse", "main~1").Trim();

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head, baseSha: stale));

        response.Outcome.Should().Be("base-moved");
        response.BaseSha.Should().Be(origin.Ref("refs/heads/main"));
    }

    [Fact]
    public async Task The_queue_branch_is_written_under_a_lease()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);
        var first = await Writer.PrepareAsync(PrepareRequest(origin, head));
        first.Outcome.Should().Be("prepared", first.Detail);

        // "Must not exist" fails now that it exists; the right expectation overwrites it.
        var again = await Writer.PrepareAsync(PrepareRequest(origin, head));
        var leased = await Writer.PrepareAsync(PrepareRequest(origin, head, expectedQueueSha: first.PreparedSha));

        again.Outcome.Should().Be("queue-branch-moved");
        leased.Outcome.Should().Be("prepared", leased.Detail);
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(leased.PreparedSha);
    }

    [Fact]
    public async Task A_rejected_credential_fails_as_auth_and_leaves_no_job_directory()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head, password: "ghs_wrong"));

        response.Outcome.Should().Be("failed");
        response.Reason.Should().Be("auth");
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty();
    }
}
