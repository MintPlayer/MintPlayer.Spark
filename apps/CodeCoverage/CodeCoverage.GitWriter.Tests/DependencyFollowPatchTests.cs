using CodeCoverage.Git.Dependencies;
using CodeCoverage.GitWriter.Endpoints;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// docs/code-coverage/dependency-companion-pins-PRD.md §7.1a F3: a <c>follow</c> record's edit passes the writer only through a
/// coverage.yml rule. The changed line, re-matched by the rule with the follower's current value and swapped to the new one,
/// must be the new line; the rule widens the allow-list to its paths, the deny-list still wins, and a record without its rule
/// is refused even on a line that names the dependency. SeoTester's real Dockerfile and compose file (at 35e9ba471), whose
/// own image tag and <c>playwright-core@</c> install follow <c>mcr.microsoft.com/playwright</c>. Real <c>git diff</c>.
/// </summary>
public class DependencyFollowPatchTests : IDisposable
{
    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private const string Leader = "mcr.microsoft.com/playwright";
    private const string Group = "playwright";
    private const string DockerfilePath = "apps/SeoTester/chromium/Dockerfile";
    private const string ComposePath = "apps/SeoTester/docker-compose.yml";

    private static readonly string Dockerfile = File.ReadAllText(BaseRepo.Fixture("DependencyFollow", "seotester", "chromium", "Dockerfile")).ReplaceLineEndings("\n");
    private static readonly string Compose = File.ReadAllText(BaseRepo.Fixture("DependencyFollow", "seotester", "docker-compose.yml")).ReplaceLineEndings("\n");

    private static readonly DependencyFollowRule ComposeRule = new(Leader, ComposePath, "seotester-chromium:(?<version>[0-9.]+)", null);
    private static readonly DependencyFollowRule InstallRule = new(Leader, "apps/SeoTester/**/Dockerfile", "playwright-core@(?<version>[0-9.]+)", null);

    private static DependencyPlanEntry LeaderEntry()
        => new(Leader, DockerfilePath, "v1.63.0-noble", "v1.64.1-noble", Group, "docker") { Manager = "dockerfile" };

    private static DependencyPlanEntry Follower(string path, string current = "1.63.0", string @new = "1.64.1")
        => new(Leader, path, current, @new, Group, "docker") { Manager = "follow" };

    private static string Moved(string text, string from, string to) => text.Contains(from, StringComparison.Ordinal)
        ? text.Replace(from, to, StringComparison.Ordinal)
        : throw new InvalidOperationException($"'{from}' is not in the fixture.");

    private string Base(params (string Path, string Content)[] extra)
        => repo.Commit([(DockerfilePath, Dockerfile), (ComposePath, Compose), .. extra]);

    private string MovedAll(string before, params (string Path, string Content)[] extra)
        => repo.Change(before, [
            (DockerfilePath, Moved(Moved(Dockerfile, "playwright:v1.63.0-noble", "playwright:v1.64.1-noble"), "playwright-core@1.63.0", "playwright-core@1.64.1")),
            (ComposePath, Moved(Compose, "seotester-chromium:1.63.0", "seotester-chromium:1.64.1")),
            .. extra]);

    private async Task<DependencyPatchVerdict> Validate(string before, string after, IReadOnlyList<DependencyFollowRule> follow, params DependencyPlanEntry[] entries)
        => await DependencyPatchValidator.ValidateAsync(repo.Diff(before, after), new DependencyPatchRequest(entries) { Follow = follow }, repo.Tree(before));

    [Fact]
    public async Task The_image_tag_and_the_install_move_with_the_base_image_under_their_rules()
    {
        var before = Base();
        var after = MovedAll(before);

        var verdict = await Validate(before, after, [ComposeRule, InstallRule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal(Group);
    }

    [Fact]
    public async Task Without_its_rule_a_follow_record_is_refused_even_on_a_line_naming_the_dependency()
    {
        // playwright-core@ names "playwright", the leader's last segment, so SubstitutionCheck alone would let it through.
        var before = Base();
        var after = MovedAll(before);

        var verdict = await Validate(before, after, [ComposeRule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_rule_of_another_dependency_admits_nothing()
    {
        var before = Base();
        var after = MovedAll(before);

        var verdict = await Validate(before, after, [ComposeRule with { Dependency = "node" }, InstallRule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Paths.Should().Equal(ComposePath);
    }

    [Fact]
    public async Task A_rule_whose_path_does_not_match_admits_nothing()
    {
        var before = Base();
        var after = MovedAll(before);

        var verdict = await Validate(before, after, [ComposeRule with { Path = "deploy/docker-compose.yml" }, InstallRule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Paths.Should().Equal(ComposePath);
    }

    [Fact]
    public async Task Another_change_in_a_follower_file_is_refused()
    {
        var before = Base();
        var after = repo.Change(before,
            (DockerfilePath, Moved(Moved(Dockerfile, "playwright:v1.63.0-noble", "playwright:v1.64.1-noble"), "playwright-core@1.63.0", "playwright-core@1.64.1")),
            (ComposePath, Moved(Moved(Compose, "seotester-chromium:1.63.0", "seotester-chromium:1.64.1"), "noreply@seotester", "admin@seotester")));

        var verdict = await Validate(before, after, [ComposeRule, InstallRule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_follower_written_with_another_version_than_planned_is_refused()
    {
        var before = Base();
        var after = repo.Change(before,
            (DockerfilePath, Moved(Moved(Dockerfile, "playwright:v1.63.0-noble", "playwright:v1.64.1-noble"), "playwright-core@1.63.0", "playwright-core@1.64.1")),
            (ComposePath, Moved(Compose, "seotester-chromium:1.63.0", "seotester-chromium:6.6.6")));

        var verdict = await Validate(before, after, [ComposeRule, InstallRule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_file_that_is_no_manifest_opens_only_to_its_rule()
    {
        const string readme = "apps/SeoTester/README.md";
        const string text = "# SeoTester\n\nRenders with Playwright 1.63.0 in its own image.\n";
        var rule = new DependencyFollowRule(Leader, "apps/SeoTester/README.md", "Playwright (?<version>[0-9.]+)", null);
        var before = Base((readme, text));
        var after = MovedAll(before, (readme, Moved(text, "1.63.0", "1.64.1")));
        DependencyPlanEntry[] entries = [LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath), Follower(readme)];

        var with = await Validate(before, after, [ComposeRule, InstallRule, rule], entries);
        var without = await Validate(before, after, [ComposeRule, InstallRule], entries);

        with.Outcome.Should().Be(DependencyPatchOutcome.Accepted, with.Detail);
        without.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        without.Reason.Should().Be(DependencyPatchReasons.OutsidePlannedFiles);
    }

    [Fact]
    public async Task The_deny_list_wins_over_a_rule()
    {
        const string props = "build/Versions.props";
        const string text = "<Project>\n  <PropertyGroup>\n    <PlaywrightVersion>1.63.0</PlaywrightVersion>\n  </PropertyGroup>\n</Project>\n";
        var rule = new DependencyFollowRule(Leader, props, "<PlaywrightVersion>(?<version>[0-9.]+)<", null);
        var before = Base((props, text));
        var after = MovedAll(before, (props, Moved(text, "1.63.0", "1.64.1")));

        var verdict = await Validate(before, after, [ComposeRule, InstallRule, rule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath), Follower(props));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
    }

    [Fact]
    public async Task A_rule_cannot_open_a_workflow_file()
    {
        const string workflow = ".github/workflows/seotester.yml";
        const string text = "jobs:\n  e2e:\n    container: ghcr.io/mintplayer/seotester-chromium:1.63.0\n";
        var rule = new DependencyFollowRule(Leader, ".github/workflows/*.yml", "seotester-chromium:(?<version>[0-9.]+)", null);
        var before = Base((workflow, text));
        var after = MovedAll(before, (workflow, Moved(text, "1.63.0", "1.64.1")));

        var verdict = await Validate(before, after, [ComposeRule, InstallRule, rule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath), Follower(workflow));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Paths.Should().Equal(workflow);
    }

    [Fact]
    public async Task On_a_line_with_two_matches_only_the_one_at_the_current_version_moves()
    {
        const string notes = "apps/SeoTester/NOTES.md";
        const string text = "Playwright 1.62.0 was flaky; Playwright 1.63.0 is current.\n";
        var rule = new DependencyFollowRule(Leader, notes, "Playwright (?<version>[0-9.]+)", null);
        var before = Base((notes, text));
        var after = MovedAll(before, (notes, Moved(text, "Playwright 1.63.0", "Playwright 1.64.1")));

        var verdict = await Validate(before, after, [ComposeRule, InstallRule, rule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath), Follower(notes));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
    }

    [Fact]
    public async Task A_group_whose_follower_did_not_move_is_partial()
    {
        var before = Base();
        var after = repo.Change(before,
            (DockerfilePath, Moved(Moved(Dockerfile, "playwright:v1.63.0-noble", "playwright:v1.64.1-noble"), "playwright-core@1.63.0", "playwright-core@1.64.1")));

        var verdict = await Validate(before, after, [ComposeRule, InstallRule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.PartialGroup);
    }

    /// <summary>F4: a Renovate <c>customManagers</c> record (manager <c>regex</c>, S1) keeps the ordinary rules: a manifest of its
    /// datasource, on a line naming the dependency. SeoTester's <c>playwright-core@</c> install passes so.</summary>
    [Fact]
    public async Task A_custom_manager_record_passes_on_a_manifest_line_naming_the_dependency()
    {
        var before = Base();
        var after = repo.Change(before,
            (DockerfilePath, Moved(Moved(Dockerfile, "playwright:v1.63.0-noble", "playwright:v1.64.1-noble"), "playwright-core@1.63.0", "playwright-core@1.64.1")));

        var verdict = await Validate(before, after, [], LeaderEntry(), Follower(DockerfilePath) with { Manager = "regex" });

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
    }

    /// <summary>F4: a report never widens the allow-list, so a <c>regex</c> record in a file that is no manifest is refused;
    /// such a pin needs a <c>follow:</c> rule.</summary>
    [Fact]
    public async Task A_custom_manager_record_outside_a_manifest_is_refused()
    {
        const string readme = "apps/SeoTester/README.md";
        const string text = "# SeoTester\n\nRenders with Playwright 1.63.0 in its own image.\n";
        var before = Base((readme, text));
        var after = repo.Change(before, (readme, Moved(text, "1.63.0", "1.64.1")));

        var verdict = await Validate(before, after, [], Follower(readme) with { Manager = "regex" });

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.OutsidePlannedFiles);
    }

    [Fact]
    public void The_wire_carries_a_rule_and_an_absent_version_stays_absent()
    {
        var rules = DependencyFields.Follow([
            new FollowRuleWire { Dependency = Leader, Path = ComposePath, Match = "seotester-chromium:(?<version>[0-9.]+)", Version = "" },
            new FollowRuleWire { Dependency = Leader, Path = "**/Dockerfile", Match = "playwright-core@(?<version>[0-9.]+)", Version = "^v(?<version>[0-9.]+)-noble$" },
        ]);

        rules.Should().NotBeNull();
        rules!.Select(r => r.Version).Should().Equal(new string?[] { null, "^v(?<version>[0-9.]+)-noble$" });
        DependencyFields.Follow(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("", ComposePath, "x(?<version>1)")]
    [InlineData(Leader, "/etc/passwd", "x(?<version>1)")]
    [InlineData(Leader, "../outside", "x(?<version>1)")]
    [InlineData(Leader, ComposePath, "x([0-9.]+)")]
    [InlineData(Leader, ComposePath, "(?<version>1)(?<version>2)")]
    [InlineData(Leader, ComposePath, "x(?<version>[")]
    [InlineData(Leader, "a\nb", "x(?<version>1)")]
    [InlineData(Leader, "   ", "x(?<version>1)")]
    public void The_wire_refuses_a_rule_the_parser_would_have_refused(string dependency, string path, string match)
        => DependencyFields.Follow([new FollowRuleWire { Dependency = dependency, Path = path, Match = match }]).Should().BeNull();

    [Fact]
    public void The_wire_refuses_more_rules_than_its_cap()
        => DependencyFields.Follow([.. Enumerable.Range(0, DependencyFields.MaxFollowRules + 1)
            .Select(_ => new FollowRuleWire { Dependency = Leader, Path = ComposePath, Match = "x(?<version>1)" })]).Should().BeNull();

    [Fact]
    public async Task A_rule_that_runs_out_of_time_refuses_the_line_instead_of_failing_the_job()
    {
        const string file = "apps/SeoTester/pins.txt";
        var line = new string('a', 60) + "!1.63.0\n";
        var rule = new DependencyFollowRule(Leader, file, "^(a|aa)+(?<version>[0-9.]+)$", null);
        var before = Base((file, line));
        var after = MovedAll(before, (file, Moved(line, "1.63.0", "1.64.1")));

        var verdict = await Validate(before, after, [ComposeRule, InstallRule, rule], LeaderEntry(), Follower(ComposePath), Follower(DockerfilePath), Follower(file));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
        verdict.Detail.Should().Contain("time");
    }
}
