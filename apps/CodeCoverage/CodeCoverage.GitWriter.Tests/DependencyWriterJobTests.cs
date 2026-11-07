using System.Net.Http.Json;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// The dependency updater's writer jobs (dependency-updates PRD R4, R14) against a served repository: the plan commit on the
/// work ref, the rolling branch's rebuild with a human commit replayed, the equal-tree no-op and the work ref's deletion. The
/// replay's own conflict matrix is host-free (<c>RollingBranchReplayTests</c> in CodeCoverage.Git.Tests).
/// </summary>
public class DependencyWriterJobTests : WriterTestBase
{
    private const string Before = "{\n  \"dependencies\": {\n    \"chalk\": \"4.1.0\"\n  }\n}\n";
    private const string After = "{\n  \"dependencies\": {\n    \"chalk\": \"4.1.2\"\n  }\n}\n";

    /// <summary>main holds package.json with chalk 4.1.0; returns it and the apply patch to 4.1.2 (a real <c>git diff</c>).</summary>
    private (Origin Origin, string Base, string Patch) Seeded()
    {
        var origin = Seed();
        origin.Checkout("main").Write("package.json", Before);
        var baseSha = origin.Commit("package.json");
        origin.Push("main");
        origin.Write("package.json", After);
        var patch = FixtureGit.Run(origin.Work, "diff", "--binary", "--", "package.json");
        FixtureGit.Run(origin.Work, "checkout", "--quiet", "--", "package.json");
        return (origin, baseSha, patch);
    }

    private static string Plan(string expectedSha, string to = "4.1.2", bool withLock = false) => DependencyContract.Serialize(new DependencyPlan
    {
        PlanId = "t-1",
        ExpectedSha = expectedSha,
        Groups = [new PlanGroup
        {
            Group = "chalk",
            Items = [new PlanItem
            {
                DepName = "chalk", PackageName = "chalk", Ecosystem = "npm", Datasource = "npm", Manager = "npm", PackageFile = "package.json",
                LockFiles = withLock ? ["package-lock.json"] : [],
                CurrentValue = "4.1.0", NewValue = to, NewVersion = to, UpdateType = "patch",
            }],
        }],
    });

    private static string Lock(string chalk) => $"{{\n  \"packages\": {{\n    \"node_modules/chalk\": {{\n      \"version\": \"{chalk}\"\n    }}\n  }}\n}}\n";

    /// <summary>main holds package.json and package-lock.json at chalk 4.1.0; the patch moves both to <paramref name="to"/>.</summary>
    private static string LockPatch(Origin origin, string to)
    {
        origin.Write("package.json", Before.Replace("4.1.0", to)).Write("package-lock.json", Lock(to));
        var patch = FixtureGit.Run(origin.Work, "diff", "--binary", "--", "package.json", "package-lock.json");
        FixtureGit.Run(origin.Work, "checkout", "--quiet", "--", "package.json", "package-lock.json");
        return patch;
    }

    [Fact]
    public async Task D_N2_a_lockfile_conflict_in_the_replay_puts_the_replayed_head_on_the_work_ref_for_a_relock()
    {
        var origin = Seed();
        origin.Checkout("main").Write("package.json", Before).Write("package-lock.json", Lock("4.1.0"));
        var baseSha = origin.Commit("package.json and lock");
        origin.Push("main");
        var first = await PostAsync("rolling", With(Job(origin, baseSha), new()
        {
            ["plan"] = Plan(baseSha, withLock: true), ["patch"] = Base64(LockPatch(origin, "4.1.2")), ["claimedGroups"] = new[] { "chalk" },
        }));
        first.Outcome.Should().Be("pushed", first.Detail);

        // A person refreshes the lockfile by hand and fixes the README; the next build moves chalk to 4.1.3.
        FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", $"{DependencyBranches.Rolling}:rolling");
        origin.Checkout("rolling");
        origin.Write("package-lock.json", Lock("4.1.2-hand")).Write("README.md", "fixed by hand\n");
        var human = origin.Commit("Refresh the lock", author: "Octo Cat", email: "octo@example.invalid");
        FixtureGit.Run(origin.Work, "push", "--quiet", "--force", "origin", $"rolling:refs/heads/{DependencyBranches.Rolling}");
        origin.Checkout("main");

        var answer = await PostAsync("rolling", With(Job(origin, baseSha), new()
        {
            ["plan"] = Plan(baseSha, to: "4.1.3", withLock: true), ["patch"] = Base64(LockPatch(origin, "4.1.3")), ["claimedGroups"] = new[] { "chalk" },
            ["expectedRollingSha"] = human, ["lastBotSha"] = first.BotSha,
        }));

        answer.Outcome.Should().Be(DependencyJobOutcomes.RelockNeeded, answer.Detail);
        answer.Paths.Should().Equal(["package-lock.json"]);
        origin.Ref($"refs/heads/{DependencyBranches.Rolling}").Should().Be(human, "nothing reaches the rolling branch before the relock");
        origin.Ref($"refs/heads/{DependencyBranches.Work}").Should().Be(answer.Sha);
        origin.InBare("show", $"{answer.Sha}:package-lock.json").Should().Contain("\"version\": \"4.1.3\"", "the bot's side, until the relock regenerates it");
        origin.InBare("show", $"{answer.Sha}:README.md").Should().Contain("fixed by hand");
        origin.InBare("rev-parse", $"{answer.Sha}^").Trim().Should().Be(answer.BotSha);
    }

    private static string Base64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    private Dictionary<string, object?> Job(Origin origin, string expectedSha, DateTimeOffset? date = null) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["baseBranch"] = "main",
        ["expectedSha"] = expectedSha,
        ["author"] = new { name = "CodeCoverage dependency updates", email = "bot@example.invalid", date = date ?? CommitterDate },
        ["committer"] = new { name = "CodeCoverage dependency updates", email = "bot@example.invalid", date = date ?? CommitterDate },
        ["message"] = "Update chalk\n",
    };

    private async Task<DependencyJobResponse> PostAsync(string job, Dictionary<string, object?> body)
    {
        var response = await Writer.Client.PostAsJsonAsync($"/jobs/dependencies/{job}", body, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!;
    }

    [Fact]
    public async Task R14_the_work_ref_is_the_planned_commit_plus_one_commit_holding_only_the_plan()
    {
        var (origin, baseSha, _) = Seeded();

        var answer = await PostAsync("work-ref", With(Job(origin, baseSha), new() { ["planJson"] = Plan(baseSha) }));

        answer.Outcome.Should().Be("pushed", answer.Detail);
        origin.Ref($"refs/heads/{DependencyBranches.Work}").Should().Be(answer.Sha);
        origin.InBare("rev-parse", $"{answer.Sha}^").Trim().Should().Be(baseSha);
        origin.InBare("diff-tree", "-r", "--name-only", "--no-commit-id", baseSha, answer.Sha!).Trim().Should().Be(DependencyFiles.PlanPath);
    }

    [Fact]
    public async Task R14_the_work_ref_is_deleted_under_its_lease()
    {
        var (origin, baseSha, _) = Seeded();
        var pushed = await PostAsync("work-ref", With(Job(origin, baseSha), new() { ["planJson"] = Plan(baseSha) }));

        using var stale = new HttpRequestMessage(HttpMethod.Delete, "/jobs/dependencies/work-ref")
        {
            Content = JsonContent.Create(With(Job(origin, baseSha), new() { ["expectedSha"] = new string('9', 40) }), options: GitWriterHost.Json),
        };
        var moved = await (await Writer.Client.SendAsync(stale)).Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json);
        moved!.Outcome.Should().Be(DependencyJobOutcomes.BranchMoved);
        origin.Ref($"refs/heads/{DependencyBranches.Work}").Should().NotBeNull();

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/jobs/dependencies/work-ref")
        {
            Content = JsonContent.Create(With(Job(origin, baseSha), new() { ["expectedSha"] = pushed.Sha }), options: GitWriterHost.Json),
        };
        (await (await Writer.Client.SendAsync(delete)).Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!.Outcome.Should().Be("deleted");
        origin.Ref($"refs/heads/{DependencyBranches.Work}").Should().BeNull();
    }

    // #568 D13: a release line's run has its own bot branches, named after the line, so it never touches the default branch's.
    [Fact]
    public async Task D13_a_line_job_writes_the_line_s_own_work_ref_and_rolling_branch()
    {
        var (origin, baseSha, patch) = Seeded();
        var line = new Dictionary<string, object?> { ["line"] = "dotnet-10.x" };

        var work = await PostAsync("work-ref", With(With(Job(origin, baseSha), line), new() { ["planJson"] = Plan(baseSha) }));
        var rolling = await PostAsync("rolling", With(With(Job(origin, baseSha), line), new()
        {
            ["plan"] = Plan(baseSha), ["patch"] = Base64(patch), ["claimedGroups"] = new[] { "chalk" },
        }));

        work.Outcome.Should().Be("pushed", work.Detail);
        rolling.Outcome.Should().Be("pushed", rolling.Detail);
        origin.Ref($"refs/heads/{DependencyBranches.WorkOf("dotnet-10.x")}").Should().Be(work.Sha);
        origin.Ref($"refs/heads/{DependencyBranches.RollingOf("dotnet-10.x")}").Should().Be(rolling.Sha);
        origin.Ref($"refs/heads/{DependencyBranches.Work}").Should().BeNull();
        origin.Ref($"refs/heads/{DependencyBranches.Rolling}").Should().BeNull();
        DependencyBranches.RollingOf("dotnet-10.x").Should().Be("mintplayer/dependencies-dotnet-10.x");
        DependencyBranches.WorkOf("dotnet-10.x").Should().Be("mintplayer/dependencies-work-dotnet-10.x");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/jobs/dependencies/work-ref")
        {
            Content = JsonContent.Create(With(With(Job(origin, baseSha), line), new() { ["expectedSha"] = work.Sha }), options: GitWriterHost.Json),
        };
        (await (await Writer.Client.SendAsync(delete)).Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!.Outcome.Should().Be("deleted");
        origin.Ref($"refs/heads/{DependencyBranches.WorkOf("dotnet-10.x")}").Should().BeNull();
    }

    [Theory]
    [InlineData("main")]
    [InlineData("dotnet-10.x/../x")]
    [InlineData("Dotnet-10.x")]
    [InlineData("dotnet-010.x")]
    public async Task D13_a_line_that_is_no_line_name_is_refused(string line)
    {
        var (origin, baseSha, _) = Seeded();
        var response = await Writer.Client.PostAsJsonAsync("/jobs/dependencies/work-ref",
            With(Job(origin, baseSha), new() { ["line"] = line, ["planJson"] = Plan(baseSha) }), GitWriterHost.Json);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("line");
    }

    [Fact]
    public async Task R4_the_rolling_branch_is_built_on_the_planned_commit_and_an_equal_rebuild_pushes_nothing()
    {
        var (origin, baseSha, patch) = Seeded();
        var body = With(Job(origin, baseSha), new()
        {
            ["plan"] = Plan(baseSha), ["patch"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch)), ["claimedGroups"] = new[] { "chalk" },
        });

        var first = await PostAsync("rolling", body);

        first.Outcome.Should().Be("pushed", first.Detail);
        first.BotSha.Should().Be(first.Sha);
        origin.Ref($"refs/heads/{DependencyBranches.Rolling}").Should().Be(first.Sha);
        origin.InBare("show", $"{first.Sha}:package.json").Should().Contain("\"chalk\": \"4.1.2\"");

        var again = await PostAsync("rolling", With(body, new() { ["expectedRollingSha"] = first.Sha, ["lastBotSha"] = first.BotSha }));

        again.Outcome.Should().Be(DependencyJobOutcomes.Unchanged, "R4: no new versions means no push and no CI");
        origin.Ref($"refs/heads/{DependencyBranches.Rolling}").Should().Be(first.Sha);
    }

    [Fact]
    public async Task R4_a_human_commit_on_the_rolling_branch_is_replayed_and_its_manifest_value_reported()
    {
        var (origin, baseSha, patch) = Seeded();
        var body = With(Job(origin, baseSha), new()
        {
            ["plan"] = Plan(baseSha), ["patch"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch)), ["claimedGroups"] = new[] { "chalk" },
        });
        var first = await PostAsync("rolling", body);

        // A person pins chalk at 4.1.1 on the rolling branch and fixes the README in the same commit.
        FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", $"{DependencyBranches.Rolling}:rolling");
        origin.Checkout("rolling");
        origin.Write("package.json", After.Replace("4.1.2", "4.1.1")).Write("README.md", "fixed by hand\n");
        var human = origin.Commit("Pin chalk", author: "Octo Cat", email: "octo@example.invalid");
        FixtureGit.Run(origin.Work, "push", "--quiet", "--force", "origin", $"rolling:refs/heads/{DependencyBranches.Rolling}");

        // The next slot builds on a moved default branch (a docs-only commit): rebuilt on the same base, the replayed
        // tree would equal the human's head and R4 pushes nothing ("force-push only if the tree differs").
        origin.Checkout("main").Write("CHANGELOG.md", "notes\n");
        var movedBase = origin.Commit("Docs");
        origin.Push("main");

        var rebuilt = await PostAsync("rolling", With(Job(origin, movedBase), new()
        {
            ["plan"] = Plan(movedBase), ["patch"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch)), ["claimedGroups"] = new[] { "chalk" },
            ["expectedRollingSha"] = human, ["lastBotSha"] = first.BotSha,
            ["author"] = new { name = "CodeCoverage dependency updates", email = "bot@example.invalid", date = CommitterDate.AddDays(7) },
            ["committer"] = new { name = "CodeCoverage dependency updates", email = "bot@example.invalid", date = CommitterDate.AddDays(7) },
        }));

        rebuilt.Outcome.Should().Be("pushed", rebuilt.Detail);
        rebuilt.Replayed.Should().Be(1);
        rebuilt.BotSha.Should().NotBe(first.BotSha);
        origin.InBare("rev-parse", $"{rebuilt.Sha}^").Trim().Should().Be(rebuilt.BotSha);
        origin.InBare("show", $"{rebuilt.Sha}:package.json").Should().Contain("\"chalk\": \"4.1.1\"", "the human's value wins");
        origin.InBare("show", $"{rebuilt.Sha}:README.md").Should().Contain("fixed by hand");
        origin.InBare("log", "-1", "--format=%an", rebuilt.Sha!).Trim().Should().Be("Octo Cat");
        var value = rebuilt.HumanValues.Should().ContainSingle().Which;
        value.DepName.Should().Be("chalk");
        value.HeldValue.Should().Be("4.1.1");
        value.ProposedValue.Should().Be("4.1.2");
        value.Commit.Should().Be(human);
    }

    [Fact]
    public async Task R4_a_human_commit_replayed_onto_an_equal_tree_pushes_nothing_but_still_reports_its_manifest_value()
    {
        var (origin, baseSha, patch) = Seeded();
        var body = With(Job(origin, baseSha), new()
        {
            ["plan"] = Plan(baseSha), ["patch"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch)), ["claimedGroups"] = new[] { "chalk" },
        });
        var first = await PostAsync("rolling", body);
        FixtureGit.Run(origin.Work, "fetch", "--quiet", "origin", $"{DependencyBranches.Rolling}:rolling");
        origin.Checkout("rolling");
        origin.Write("package.json", After.Replace("4.1.2", "4.1.1"));
        var human = origin.Commit("Pin chalk", author: "Octo Cat", email: "octo@example.invalid");
        FixtureGit.Run(origin.Work, "push", "--quiet", "--force", "origin", $"rolling:refs/heads/{DependencyBranches.Rolling}");

        var again = await PostAsync("rolling", With(body, new() { ["expectedRollingSha"] = human, ["lastBotSha"] = first.BotSha }));

        again.Outcome.Should().Be(DependencyJobOutcomes.Unchanged, "R4: force-push only if the tree differs");
        origin.Ref($"refs/heads/{DependencyBranches.Rolling}").Should().Be(human);
        again.Replayed.Should().Be(1);
        again.HumanValues.Should().ContainSingle().Which.HeldValue.Should().Be("4.1.1", "the human's value still becomes a hold");
    }

    [Fact]
    public async Task R4_a_rolling_branch_rewritten_by_hand_is_kept()
    {
        var (origin, baseSha, patch) = Seeded();
        var body = With(Job(origin, baseSha), new()
        {
            ["plan"] = Plan(baseSha), ["patch"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch)), ["claimedGroups"] = new[] { "chalk" },
        });
        var first = await PostAsync("rolling", body);
        origin.SetBranch(DependencyBranches.Rolling, baseSha);

        var answer = await PostAsync("rolling", With(body, new() { ["expectedRollingSha"] = baseSha, ["lastBotSha"] = first.BotSha }));

        answer.Outcome.Should().Be(DependencyJobOutcomes.RollingRewritten);
        origin.Ref($"refs/heads/{DependencyBranches.Rolling}").Should().Be(baseSha);
    }

    [Fact]
    public async Task A_patch_outside_the_plan_is_refused_before_anything_is_pushed()
    {
        var (origin, baseSha, _) = Seeded();
        origin.Write("README.md", "changed\n");
        var patch = FixtureGit.Run(origin.Work, "diff", "--binary", "--", "README.md");

        var answer = await PostAsync("rolling", With(Job(origin, baseSha), new()
        {
            ["plan"] = Plan(baseSha), ["patch"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(patch)), ["claimedGroups"] = new[] { "chalk" },
        }));

        answer.Outcome.Should().Be("patch-rejected");
        origin.Ref($"refs/heads/{DependencyBranches.Rolling}").Should().BeNull();
    }

    [Fact]
    public async Task Validate_runs_the_dependencies_profile_and_pushes_nothing()
    {
        var (origin, baseSha, patch) = Seeded();

        var accepted = await PostAsync("validate", With(Job(origin, baseSha), new()
        {
            ["plan"] = Plan(baseSha), ["patch"] = Base64(patch), ["claimedGroups"] = new[] { "chalk" }, ["allowedHosts"] = new[] { "npm.example.com" },
        }));
        var emptyButClaimed = await PostAsync("validate", With(Job(origin, baseSha), new()
        {
            ["plan"] = Plan(baseSha), ["patch"] = "", ["claimedGroups"] = new[] { "chalk" },
        }));
        var emptyAndNothingClaimed = await PostAsync("validate", With(Job(origin, baseSha), new() { ["plan"] = Plan(baseSha), ["patch"] = "" }));

        accepted.Outcome.Should().Be(DependencyJobOutcomes.Validated, accepted.Detail);
        accepted.Verdict.Should().Be(DependencyJobOutcomes.Accepted, accepted.Detail);
        emptyButClaimed.Verdict.Should().Be(DependencyJobOutcomes.Rejected);
        emptyAndNothingClaimed.Verdict.Should().Be(DependencyJobOutcomes.Accepted);
        origin.Ref($"refs/heads/{DependencyBranches.Rolling}").Should().BeNull();
        origin.Ref($"refs/heads/{DependencyBranches.Work}").Should().BeNull();
    }

    [Fact]
    public async Task R16_the_onboarding_files_are_one_commit_on_the_planned_commit()
    {
        var (origin, baseSha, _) = Seeded();

        var answer = await PostAsync("files", With(Job(origin, baseSha), new()
        {
            ["files"] = new object[] { new { path = "coverage.yml", content = "dependencies:\n  enabled: true\n" } },
        }));

        answer.Outcome.Should().Be("pushed", answer.Detail);
        answer.Paths.Should().Equal(["coverage.yml"]);
        origin.Ref($"refs/heads/{DependencyBranches.Onboarding}").Should().Be(answer.Sha);
        origin.InBare("rev-parse", $"{answer.Sha}^").Trim().Should().Be(baseSha);
        origin.InBare("show", $"{answer.Sha}:coverage.yml").Should().Contain("enabled: true");
    }

    /// <summary>Each refusal names its field and is answered before anything is cloned.</summary>
    [Theory]
    [InlineData("work-ref", "both-plan-and-patch", "planJson")]
    [InlineData("work-ref", "plan-for-another-commit", "planJson")]
    [InlineData("work-ref", "bad-work-sha", "expectedWorkSha")]
    [InlineData("work-ref", "staged-without-plan", "plan")]
    [InlineData("validate", "no-plan", "plan")]
    [InlineData("validate", "bad-base64", "patch")]
    [InlineData("validate", "control-char-group", "claimedGroups")]
    [InlineData("validate", "bad-host", "allowedHosts")]
    [InlineData("validate", "empty-action-key", "targetActions")]
    [InlineData("validate", "bad-expected-sha", "expectedSha")]
    [InlineData("validate", "no-credential", "credential")]
    [InlineData("rolling", "too-many-migrations", "migrations")]
    [InlineData("rolling", "unnamed-migration", "migrations")]
    [InlineData("rolling", "bad-last-bot-sha", "lastBotSha")]
    [InlineData("rolling", "bad-rolling-sha", "expectedRollingSha")]
    [InlineData("rolling", "no-message", "message")]
    [InlineData("files", "no-files", "files")]
    [InlineData("files", "outside-the-onboarding-set", "files")]
    [InlineData("files", "same-file-twice", "files")]
    [InlineData("files", "delete-with-content", "files")]
    [InlineData("files", "neither-content-nor-delete", "files")]
    [InlineData("files", "seventeen-files", "files")]
    public async Task A_malformed_dependency_job_is_refused_with_its_field(string job, string variant, string field)
    {
        var origin = Seed();
        var sha = new string('a', 40);
        var plan = Plan(sha);
        var patch = Base64("diff --git a/package.json b/package.json\n");
        Dictionary<string, object?> body = variant switch
        {
            "both-plan-and-patch" => new() { ["planJson"] = plan, ["patch"] = patch, ["plan"] = plan },
            "plan-for-another-commit" => new() { ["planJson"] = Plan(new string('b', 40)) },
            "bad-work-sha" => new() { ["planJson"] = plan, ["expectedWorkSha"] = "main" },
            "staged-without-plan" => new() { ["patch"] = patch },
            "no-plan" => new() { ["patch"] = patch },
            "bad-base64" => new() { ["plan"] = plan, ["patch"] = "not base64!" },
            "control-char-group" => new() { ["plan"] = plan, ["patch"] = patch, ["claimedGroups"] = new[] { "chalk\n" } },
            "bad-host" => new() { ["plan"] = plan, ["patch"] = patch, ["allowedHosts"] = new[] { "not a host" } },
            "empty-action-key" => new() { ["plan"] = plan, ["patch"] = patch, ["targetActions"] = new Dictionary<string, string> { [""] = "name: x" } },
            "bad-expected-sha" => new() { ["expectedSha"] = "HEAD", ["plan"] = plan, ["patch"] = patch },
            "no-credential" => new() { ["credential"] = null, ["plan"] = plan, ["patch"] = patch },
            "too-many-migrations" => new() { ["plan"] = plan, ["patch"] = patch, ["migrations"] = Enumerable.Range(0, 9).Select(i => new { group = $"g{i}", patch }).ToArray() },
            "unnamed-migration" => new() { ["plan"] = plan, ["patch"] = patch, ["migrations"] = new[] { new { group = "", patch } } },
            "bad-last-bot-sha" => new() { ["plan"] = plan, ["patch"] = patch, ["lastBotSha"] = "x" },
            "bad-rolling-sha" => new() { ["plan"] = plan, ["patch"] = patch, ["expectedRollingSha"] = "x" },
            "no-message" => new() { ["plan"] = plan, ["patch"] = patch, ["message"] = null },
            "no-files" => new() { ["files"] = Array.Empty<object>() },
            "outside-the-onboarding-set" => new() { ["files"] = new object[] { new { path = "README.md", content = "x" } } },
            "same-file-twice" => new() { ["files"] = new object[] { new { path = "coverage.yml", content = "a" }, new { path = "coverage.yml", content = "b" } } },
            "delete-with-content" => new() { ["files"] = new object[] { new { path = "coverage.yml", content = "a", delete = true } } },
            "neither-content-nor-delete" => new() { ["files"] = new object[] { new { path = "coverage.yml" } } },
            "seventeen-files" => new() { ["files"] = Enumerable.Range(0, 17).Select(_ => (object)new { path = "coverage.yml", content = "a" }).ToArray() },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var response = await Writer.Client.PostAsJsonAsync($"/jobs/dependencies/{job}", With(Job(origin, sha), body), GitWriterHost.Json);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(field);
    }

    [Theory]
    [InlineData("work-ref")]
    [InlineData("validate")]
    [InlineData("rolling")]
    [InlineData("files")]
    public async Task A_body_that_is_not_json_is_refused(string job)
    {
        using var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");
        var response = await Writer.Client.PostAsync($"/jobs/dependencies/{job}", content);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("expectedSha", "HEAD", "expectedSha")]
    [InlineData("line", "main", "line")]
    [InlineData("repoId", "", "repoId")]
    public async Task Deleting_the_work_ref_refuses_a_malformed_request(string key, string value, string field)
    {
        var origin = Seed();
        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/jobs/dependencies/work-ref")
        {
            Content = JsonContent.Create(With(Job(origin, new string('a', 40)), new() { [key] = value }), options: GitWriterHost.Json),
        };
        var response = await Writer.Client.SendAsync(delete);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(field);

        using var garbage = new HttpRequestMessage(HttpMethod.Delete, "/jobs/dependencies/work-ref")
        {
            Content = new StringContent("[", System.Text.Encoding.UTF8, "application/json"),
        };
        (await Writer.Client.SendAsync(garbage)).StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }
}
