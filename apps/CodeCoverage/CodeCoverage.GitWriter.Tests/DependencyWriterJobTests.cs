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
}
