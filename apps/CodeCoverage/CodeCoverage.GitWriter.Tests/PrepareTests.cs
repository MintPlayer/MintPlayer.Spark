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
    /// D12, §0 D63: unless the saga allows it (a same-repository pull request), a change under <c>.github/**</c> is refused.
    /// A fork's prepared commit is the one the queue would push to <c>mq/</c> for a dispatch, and that push alone would
    /// start the base repository's <c>push</c> workflows from the pushed commit's own workflow files, with its secrets.
    /// </summary>
    [Theory]
    [InlineData(".github/workflows/regenerate.yml")]
    [InlineData(".GitHub/actions/x/action.yml")]
    public async Task A_change_under_github_is_refused_unless_the_saga_allows_it(string path)
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
        origin.Ref($"refs/heads/{QueueBranch}").Should().BeNull("§0 D65: only the queue-branch push writes there");
    }

    /// <summary>
    /// §0 D65: the guard reads the merged tree, not the pull request's net change. The base renamed a template into
    /// <c>.github/workflows/</c> after the pull request branched, and the pull request edited the template where it used
    /// to be: rename detection carries the edit into the workflow file, although the net change names no
    /// <c>.github/</c> path. Before, the writer looked at the net change only and prepared this fork's commit as not
    /// touching workflows, so the fork's <c>on: push</c> workflow would have run on the queue branch.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_change_a_base_rename_carries_into_github_touches_the_workflows(bool allowWorkflowChanges)
    {
        const string template = "docs/ci-template.yml";
        var origin = Seed();
        origin.Checkout("main").Write(template, "name: ci\non: workflow_dispatch\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo template\n");
        origin.Commit("main: a workflow template");
        origin.Push("main");
        FixtureGit.Run(origin.Work, "checkout", "--quiet", "-B", "feature", "main");
        origin.Write(template, "name: ci\non: push\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo pull-request-code\n");
        var head = origin.Commit("pr: edit the template");
        origin.Push("feature").PullRequest(1, "feature");
        origin.Checkout("main");
        var content = File.ReadAllText(Path.Combine(origin.Work, template));
        File.Delete(Path.Combine(origin.Work, template));
        origin.Write(".github/workflows/ci.yml", content);
        origin.Commit("main: the template becomes a workflow");
        origin.Push("main");
        FixtureGit.Run(origin.Bare, "diff-tree", "-r", "--no-renames", "--name-only", "main~1", head).Trim()
            .Should().Be("docs/ci-template.yml", "the pull request's own change is outside .github/");

        var response = await Writer.PrepareAsync(PrepareRequest(origin, head, allowWorkflowChanges: allowWorkflowChanges));

        if (allowWorkflowChanges)
        {
            response.Outcome.Should().Be("prepared", response.Detail);
            response.TouchesWorkflows.Should().BeTrue();
        }
        else
        {
            response.Outcome.Should().Be("evicted", response.Detail);
            response.Reason.Should().Be("touches-workflows");
        }
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
