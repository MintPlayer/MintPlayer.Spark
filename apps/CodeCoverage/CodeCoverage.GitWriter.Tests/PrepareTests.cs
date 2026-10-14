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

    /// <summary>
    /// D12, §0 D63: unless the saga allows it (a same-repository pull request), a change under <c>.github/**</c> is refused
    /// before anything is pushed. The push to <c>mq/</c> alone would start the base repository's <c>push</c> workflows from
    /// the pushed commit's own workflow files, with the base repository's secrets.
    /// </summary>
    [Theory]
    [InlineData(".github/workflows/regenerate.yml")]
    [InlineData(".GitHub/actions/x/action.yml")]
    public async Task A_change_under_github_is_refused_before_any_push_unless_the_saga_allows_it(string path)
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

    /// <summary>
    /// §0 D63 (O21 A): for a same-repository pull request the writer prepares the commit and reports the paths; whether
    /// the entry may go on is the saga's call, because only it knows whether it will dispatch the regenerate workflow.
    /// </summary>
    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".GitHub/dependabot.yml")]
    public async Task A_change_under_github_is_prepared_and_reported_when_the_saga_allows_it(string path)
    {
        var origin = Seed();
        origin.Write(path, "on: push\n");
        var head = origin.Commit("pr: touch workflows");
        origin.Push("feature").PullRequest(1, "feature");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head, allowWorkflowChanges: true));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.TouchesWorkflows.Should().BeTrue();
        response.Paths.Should().Equal([path]);
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(response.PreparedSha);
    }

    /// <summary>
    /// §0 D59: a fork that has only the pull request's head lacks the workflow files the base changed since, and GitHub
    /// refuses an App push that brings workflow files the target repository has on no branch unless the token carries
    /// <c>workflows: write</c>. The prepare says whether the base changed them, so the publish token asks for it only then.
    /// </summary>
    [Fact]
    public async Task The_prepare_reports_whether_the_base_changed_its_workflows_since_the_head()
    {
        var origin = Seed();
        AddEntity(origin, "Order", "with-order");
        var head = origin.Commit("Add Order");
        origin.Push("feature").PullRequest(1, "feature");
        origin.Checkout("main").Write("README.md", "seed\nmain moved\n");
        origin.Commit("main: readme");
        origin.Push("main");

        var unchanged = await Writer.PrepareAsync(PrepareRequest(origin, head));
        unchanged.Outcome.Should().Be("prepared", unchanged.Detail);
        unchanged.BaseChangedWorkflows.Should().BeFalse();
        unchanged.TouchesWorkflows.Should().BeFalse();

        origin.Write(".github/workflows/ci.yml", "on: push\n");
        origin.Commit("main: a workflow");
        origin.Push("main");

        var changed = await Writer.PrepareAsync(PrepareRequest(origin, head, expectedQueueSha: unchanged.PreparedSha));
        changed.Outcome.Should().Be("prepared", changed.Detail);
        changed.BaseChangedWorkflows.Should().BeTrue();
        changed.TouchesWorkflows.Should().BeFalse("the pull request's own change does not touch .github");
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
        // Something that is not this job's commit holds the branch: the base itself.
        var foreign = origin.Ref("refs/heads/main")!;
        origin.SetBranch(QueueBranch, foreign);

        // "Must not exist" fails now that it exists; the right expectation overwrites it.
        var again = await Writer.PrepareAsync(PrepareRequest(origin, head));
        again.Outcome.Should().Be("queue-branch-moved");
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(foreign, "a rejected lease writes nothing");

        var leased = await Writer.PrepareAsync(PrepareRequest(origin, head, expectedQueueSha: foreign));
        leased.Outcome.Should().Be("prepared", leased.Detail);
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(leased.PreparedSha);
    }

    /// <summary>
    /// D51 (1) for prepare: the push landed but the saga never saved it, so it asks again with the old lease. The queue
    /// branch holds the earlier attempt's commit (same tree, same parent, the bot's committer, another committer date),
    /// which is the answer, instead of a reset of the branch and a second prepare.
    /// </summary>
    [Fact]
    public async Task A_retried_prepare_recognises_the_commit_an_earlier_attempt_pushed()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);
        var first = await Writer.PrepareAsync(PrepareRequest(origin, head));
        first.Outcome.Should().Be("prepared", first.Detail);
        var earlier = origin.Rewrite(first.PreparedSha!, CommitText.Redated);
        origin.SetBranch(QueueBranch, earlier);

        var again = await Writer.PrepareAsync(PrepareRequest(origin, head));

        again.Outcome.Should().Be("prepared", again.Detail);
        again.Reason.Should().Be("already-pushed");
        again.PreparedSha.Should().Be(earlier);
        again.ResolvedPaths.Should().Equal([ModelHashes.Path], "the saga still learns what was resolved");
        origin.Ref($"refs/heads/{QueueBranch}").Should().Be(earlier);
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
