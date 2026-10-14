using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// <c>POST /jobs/prepare</c> (docs/coverage_merge_queue_PRD.md §5.1 step [1], D5, D12, T3, §0 D65) against a local
/// smart-HTTP forge: a real blob-less clone, a real fetch of <c>refs/pull/1/head</c>, the shared resolver and
/// <c>commit-tree</c>. Nothing is pushed: the queue branch is the queue-branch push's (<see cref="QueueBranchTests"/>).
/// </summary>
public class PrepareTests : WriterTestBase
{
    /// <summary>
    /// §0 D65: the prepare pushes nothing, so a fork's code reaches no branch of the base repository unless a dispatch
    /// needs it. Its commit is a function of the request (the dates are the saga's), so the same request answers the same
    /// commit, which is what lets the queue-branch push and the publish rebuild it instead of reading it from mq/.
    /// </summary>
    [Fact]
    public async Task A_prepare_pushes_nothing_and_answers_the_same_commit_every_time()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);
        var refs = origin.InBare("for-each-ref", "--format=%(refname) %(objectname)");

        var first = await Writer.PrepareAsync(PrepareRequest(origin, head));
        var second = await Writer.PrepareAsync(PrepareRequest(origin, head));

        first.Outcome.Should().Be("prepared", first.Detail);
        second.Outcome.Should().Be("prepared", second.Detail);
        second.Reason.Should().BeNull("nothing was pushed, so there is nothing to recognise");
        second.PreparedSha.Should().Be(first.PreparedSha, "the commit is a function of the request");
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull();
        origin.InBare("for-each-ref", "--format=%(refname) %(objectname)").Should().Be(refs, "the forge is left exactly as it was");
        FixtureGit.TryRun(origin.Bare, null, "cat-file", "-e", first.PreparedSha!).Code.Should().NotBe(0, "the commit never reached the forge");
    }

    [Fact]
    public async Task A_clean_merge_of_a_fork_head_is_squashed_onto_the_base_without_touching_the_forge()
    {
        var origin = Seed();
        // A fork's pull request: the head exists only as refs/pull/1/head.
        AddEntity(origin, "Order", "with-order");
        var head = origin.Commit("Add Order");
        origin.PullRequest(1, "feature");
        origin.Checkout("main").Write("README.md", "seed\nmain moved\n");
        var baseSha = origin.Commit("main: readme");
        origin.Push("main");
        var refs = origin.InBare("for-each-ref", "--format=%(refname) %(objectname)");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head, baseSha: baseSha));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.BaseSha.Should().Be(baseSha);
        response.HeadSha.Should().Be(head);
        response.ResolvedPaths.Should().BeEmpty();
        response.TouchesWorkflows.Should().BeFalse();
        origin.InBare("for-each-ref", "--format=%(refname) %(objectname)").Should().Be(refs);
        Directory.GetFileSystemEntries(Writer.Root).Should().BeEmpty("the job directory is deleted when the job ends");
    }

    [Fact]
    public async Task A_roll_up_conflict_is_resolved_to_the_base_side_and_the_path_is_reported()
    {
        var origin = Seed();
        var head = RollUpConflict(origin);

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.ResolvedPaths.Should().Equal([ModelHashes.Path]);
        response.BaseSha.Should().Be(origin.Ref("refs/heads/main"));
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
    /// O25, §0 D67: the prepare pushes nothing (§0 D65), so it refuses no change under <c>.github/**</c>. It prepares the
    /// commit and reports the paths, a fork's included, and the saga decides: such a change stops the entry only when the
    /// regenerate workflow is to be dispatched (<c>MergeQueueWorkflowGuard</c>), and the queue-branch push refuses a
    /// fork's itself (<see cref="QueueBranchTests"/>). Before, a request without <c>allowWorkflowChanges</c>, which the
    /// saga sent for every fork, was evicted <c>touches-workflows</c> here.
    /// </summary>
    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/regenerate.yml")]
    [InlineData(".GitHub/actions/x/action.yml")]
    [InlineData(".GitHub/dependabot.yml")]
    public async Task A_change_under_github_is_prepared_and_reported_a_forks_too(string path)
    {
        var origin = Seed();
        // A fork's pull request: the head exists only as refs/pull/1/head.
        origin.Write(path, "on: push\n");
        var head = origin.Commit("pr: touch workflows");
        origin.PullRequest(1, "feature");
        var refs = origin.InBare("for-each-ref", "--format=%(refname) %(objectname)");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.TouchesWorkflows.Should().BeTrue();
        response.Paths.Should().Equal([path]);
        origin.InBare("for-each-ref", "--format=%(refname) %(objectname)").Should().Be(refs, "§0 D65: only the queue-branch push writes to the base repository");
    }

    /// <summary>
    /// §0 D65: the report reads the merged tree, not the pull request's net change. The base renamed a template into
    /// <c>.github/workflows/</c> after the pull request branched, and the pull request edited the template where it used
    /// to be: rename detection carries the edit into the workflow file, although the net change names no
    /// <c>.github/</c> path. Before D65, the writer looked at the net change only and reported nothing.
    /// </summary>
    [Fact]
    public async Task A_change_a_base_rename_carries_into_github_touches_the_workflows()
    {
        var origin = Seed();
        var head = BaseRenameCarriesIntoGithub(origin);

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.TouchesWorkflows.Should().BeTrue();
        response.Paths.Should().Equal([".github/workflows/ci.yml"]);
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

        var changed = await Writer.PrepareAsync(PrepareRequest(origin, head));
        changed.Outcome.Should().Be("prepared", changed.Detail);
        changed.BaseChangedWorkflows.Should().BeTrue();
        changed.TouchesWorkflows.Should().BeFalse("the commit holds the base's .github/ unchanged");
    }

    /// <summary>
    /// §0 D59, O25, D67: the two reports split what a fork's publish brings into the fork's branch, which holds the pinned
    /// head. <c>baseChangedWorkflows</c> is the prepared tree's <c>.github/workflows</c> against the head's: what the base
    /// changed. <c>touchesWorkflows</c> is the prepared tree's <c>.github/</c> against the base's: the pull request's own
    /// change, which O25 lets a fork's publish carry. Before, <c>baseChangedWorkflows</c> compared the base with the head,
    /// so a change of the head alone set it too.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_workflow_change_of_the_head_is_the_pull_requests_and_one_of_the_base_is_the_bases(bool baseChanges)
    {
        var origin = Seed();
        origin.Write(".github/workflows/ci.yml", "on: push\n");
        var head = origin.Commit("pr: a workflow");
        origin.PullRequest(1, "feature");
        origin.Checkout("main").Write("README.md", "seed\nmain moved\n");
        if (baseChanges)
            origin.Write(".github/workflows/release.yml", "on: workflow_dispatch\n");
        origin.Commit("main: moved");
        origin.Push("main");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head));

        response.Outcome.Should().Be("prepared", response.Detail);
        response.TouchesWorkflows.Should().BeTrue();
        response.Paths.Should().Equal([".github/workflows/ci.yml"]);
        response.BaseChangedWorkflows.Should().Be(baseChanges, "the prepared tree holds the head's own workflow change, which a fork's branch has already");
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
        response.Detail.Should().Contain(pinned).And.NotContain("T3", "the detail reaches the eviction message, whose readers never saw the PRD (§0 D72)");
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
