using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.Tests.Dependencies.Policy;
using Xunit;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>R6/§7.5: the comment commands, as the normaliser reads them.</summary>
public class DependencyCommandParserTests
{
    [Theory]
    [InlineData("approve angular", EDependencyCommand.Approve, "angular")]
    [InlineData("  Approve `@angular/*`\nthanks", EDependencyCommand.Approve, "@angular/*")]
    [InlineData("hold lodash", EDependencyCommand.Hold, "lodash")]
    [InlineData("unhold lodash", EDependencyCommand.Unhold, "lodash")]
    [InlineData("bisect", EDependencyCommand.Bisect, null)]
    [InlineData("refresh\r\n", EDependencyCommand.Refresh, null)]
    public void A_command_is_the_first_word_of_the_first_line(string body, EDependencyCommand command, string? argument)
    {
        var parsed = DependencyCommandParser.Parse(body);
        parsed.Should().NotBeNull();
        parsed!.Value.Command.Should().Be(command);
        parsed.Value.Argument.Should().Be(argument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I approve of this")]
    [InlineData("approve")]
    [InlineData("hold")]
    [InlineData("refresh please")]
    [InlineData("/queue")]
    public void Anything_else_is_an_ordinary_comment(string body)
        => DependencyCommandParser.Parse(body).Should().BeNull();

    [Fact]
    public void An_overlong_argument_is_not_a_command()
        => DependencyCommandParser.Parse("approve " + new string('a', DependencyCommandParser.MaxArgumentLength + 1)).Should().BeNull();
}

/// <summary>Q8: the slot, in the account's time zone, spread within the hour, one key per repository and slot.</summary>
public class DependencySlotsTests
{
    private const string Repo = "Repositories/github/910040";

    [Fact]
    public void A_weekly_slot_is_due_from_its_spread_minute_to_the_end_of_its_hour()
    {
        var schedule = new DependencySchedule(EDependencyScheduleFrequency.Weekly, EDependencyWeekday.Monday, 6, "UTC");
        var minute = DependencySlots.SpreadMinute(Repo);
        var monday = new DateTimeOffset(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);

        DependencySlots.Due(schedule, monday.AddMinutes(minute), Repo).Due.Should().BeTrue();
        DependencySlots.Due(schedule, monday.AddMinutes(59), Repo).Due.Should().BeTrue();
        DependencySlots.Due(schedule, monday.AddHours(1), Repo).Due.Should().BeFalse();
        DependencySlots.Due(schedule, monday.AddDays(1).AddMinutes(minute), Repo).Due.Should().BeFalse("Tuesday is not the slot");
        if (minute > 0)
            DependencySlots.Due(schedule, monday.AddMinutes(minute - 1), Repo).Due.Should().BeFalse("before the spread minute");
    }

    [Fact]
    public void The_key_is_one_per_repository_and_day_so_a_slot_runs_once()
    {
        var schedule = new DependencySchedule(EDependencyScheduleFrequency.Daily, EDependencyWeekday.Monday, 6, "UTC");
        var at = new DateTimeOffset(2026, 10, 19, 6, 59, 0, TimeSpan.Zero);
        DependencySlots.Due(schedule, at, Repo).Key.Should().Be(DependencySlots.Due(schedule, at.AddMinutes(-1), Repo).Key);
        DependencySlots.Due(schedule, at, Repo).Key.Should().NotBe(DependencySlots.Due(schedule, at, "Repositories/github/1").Key);
    }

    [Fact]
    public void A_monthly_slot_is_the_first_such_weekday()
    {
        var schedule = new DependencySchedule(EDependencyScheduleFrequency.Monthly, EDependencyWeekday.Monday, 6, "UTC");
        var minute = DependencySlots.SpreadMinute(Repo);
        DependencySlots.Due(schedule, new DateTimeOffset(2026, 11, 2, 6, minute, 0, TimeSpan.Zero), Repo).Due.Should().BeTrue();
        DependencySlots.Due(schedule, new DateTimeOffset(2026, 11, 9, 6, minute, 0, TimeSpan.Zero), Repo).Due.Should().BeFalse();
    }

    [Fact]
    public void The_hour_is_the_accounts_time_zone_and_an_unknown_zone_is_utc()
    {
        var brussels = new DependencySchedule(EDependencyScheduleFrequency.Daily, EDependencyWeekday.Monday, 6, "Europe/Brussels");
        var minute = DependencySlots.SpreadMinute(Repo);
        // 2026-11-02 is after the end of summer time (CET, +01:00): 06:xx local is 05:xx UTC.
        DependencySlots.Due(brussels, new DateTimeOffset(2026, 11, 2, 5, minute, 0, TimeSpan.Zero), Repo).Due.Should().BeTrue();
        DependencySlots.Due(brussels, new DateTimeOffset(2026, 11, 2, 6, minute, 0, TimeSpan.Zero), Repo).Due.Should().BeFalse();
        DependencySlots.Zone("Not/AZone").Should().Be(TimeZoneInfo.Utc);
    }
}

/// <summary>R11: the coverage delta of the rolling head, and what it does to auto-merge.</summary>
public class CoverageDeltaGateTests
{
    private static Commit Head(double? delta, string? completeness = CommitAssembly.Complete)
        => new() { Coverage = new CoverageSummary { LinesCovered = 80, LinesCoverable = 100 }, CoverageDeltaVsDefaultBranch = delta, AssemblyCompleteness = completeness };

    [Fact]
    public void A_drop_within_the_limit_passes()
        => DependencyCoverageGate.Evaluate(Head(-0.4), 0.5).Gate.Should().Be(ECoverageGate.Passed);

    [Fact]
    public void A_drop_beyond_the_limit_holds()
        => DependencyCoverageGate.Evaluate(Head(-0.6), 0.5).Gate.Should().Be(ECoverageGate.Dropped);

    [Fact]
    public void An_incomplete_report_is_its_own_hold_never_a_drop()
        => DependencyCoverageGate.Evaluate(Head(-10, CommitAssembly.Partial), 0.5).Gate.Should().Be(ECoverageGate.Incomplete);

    [Fact]
    public void No_coverage_data_skips_the_gate()
    {
        DependencyCoverageGate.Evaluate(null, 0.5).Gate.Should().Be(ECoverageGate.NoData);
        DependencyCoverageGate.Evaluate(Head(null), 0.5).Gate.Should().Be(ECoverageGate.NoData);
    }

    [Fact]
    public void The_note_states_the_delta_in_points()
        => DependencyCoverageGate.Evaluate(Head(0.25), 0).Note.Should().Contain("+0.25 pp");
}

/// <summary>R5, R11, R15, R16: what keeps the rolling pull request out of the merge queue.</summary>
public class DependencyAutoMergeGateTests
{
    private static DependencyRunReport Report(params DependencyReportGroup[] groups) => new() { Groups = [.. groups] };

    private static DependencyReportGroup Group(string name, EUpdateLevel level = EUpdateLevel.Minor, bool autoMerge = true, bool platform = false)
        => new(name, level, autoMerge, false, false, null, platform, [new DependencyReportItem(name, "package.json", "npm", "npm", "1.0.0", "1.1.0", "1.1.0", null)]);

    private static DependencyEnqueueInput Input(DependencyRunReport report, bool queue = true, bool draft = false, bool human = false, bool migrations = false,
        IReadOnlyList<string>? workflows = null, ECoverageGate coverage = ECoverageGate.Passed)
        => new(queue, report, draft, human, migrations, workflows ?? [], coverage);

    [Fact]
    public void Green_within_every_threshold_enqueues()
        => DependencyAutoMergeGate.Refusal(Input(Report(Group("chalk")))).Should().BeNull();

    [Fact]
    public void Without_the_merge_queue_nothing_auto_merges()
        => DependencyAutoMergeGate.Refusal(Input(Report(Group("chalk")), queue: false)).Should().Contain("merge queue");

    [Fact]
    public void A_draft_never_auto_merges()
        => DependencyAutoMergeGate.Refusal(Input(Report(Group("chalk")), draft: true)).Should().Contain("draft");

    [Fact]
    public void An_over_threshold_group_waits()
        => DependencyAutoMergeGate.Refusal(Input(Report(Group("chalk"), Group("nx", EUpdateLevel.Major, autoMerge: false)))).Should().Contain("`nx`");

    [Fact]
    public void A_platform_major_never_auto_merges()
        => DependencyAutoMergeGate.Refusal(Input(Report(Group("platform:dotnet", EUpdateLevel.Major, platform: true)))).Should().Contain("platform major");

    [Fact]
    public void Migrations_never_auto_merge()
        => DependencyAutoMergeGate.Refusal(Input(Report(Group("chalk")), migrations: true)).Should().Contain("migrations");

    [Fact]
    public void A_workflow_not_run_on_pull_request_never_auto_merges_its_edit()
        => DependencyAutoMergeGate.Refusal(Input(Report(Group("actions/checkout")), workflows: [".github/workflows/release.yml"])).Should().Contain("release.yml");

    [Theory]
    [InlineData(ECoverageGate.Dropped, false)]
    [InlineData(ECoverageGate.Incomplete, false)]
    [InlineData(ECoverageGate.NoData, true)]
    public void The_coverage_gate(ECoverageGate coverage, bool enqueued)
        => (DependencyAutoMergeGate.Refusal(Input(Report(Group("chalk")), coverage: coverage)) is null).Should().Be(enqueued);

    [Fact]
    public void Too_many_new_packages_wait_for_a_person()
        => DependencyAutoMergeGate.Refusal(Input(Report(Group("chalk")), human: true)).Should().Contain("new packages");
}

/// <summary>Old §7.4's suspicion order and the cumulative leave-out.</summary>
public class DependencyBisectPlannerTests
{
    private static DependencyReportGroup Group(string name, EUpdateLevel level, string version = "2.0.0")
        => new(name, level, true, false, false, null, false, [new DependencyReportItem(name, "package.json", "npm", "npm", "1.0.0", version, version, null)]);

    [Fact]
    public void Majors_first_then_versions_red_elsewhere_then_the_later_plan_groups()
    {
        var report = new DependencyRunReport { Groups = [Group("a", EUpdateLevel.Patch), Group("b", EUpdateLevel.Minor), Group("c", EUpdateLevel.Minor, "3.1.0"), Group("d", EUpdateLevel.Major)] };
        var fleet = new DependencyFleetState { RedVersions = [new DependencyFleetRed { Ecosystem = "npm", DepName = "a", Version = "2.0.0", Repository = "Repositories/github/2" }] };

        DependencyBisectPlanner.Suspects(report, fleet).Should().Equal(["d", "a", "c", "b"]);
    }

    [Fact]
    public void Each_step_leaves_one_more_suspect_out_within_the_budget()
    {
        var bisect = new DependencyBisect { Suspects = ["d", "c", "b", "a"], Budget = 3, Spent = 1 };
        DependencyBisectPlanner.NextExclusion(bisect).Should().Equal(["d"]);
        bisect.Excluded = ["d"];
        bisect.Spent = 2;
        DependencyBisectPlanner.NextExclusion(bisect).Should().Equal(["d", "c"]);
        bisect.Excluded = ["d", "c"];
        bisect.Spent = 3;
        DependencyBisectPlanner.NextExclusion(bisect).Should().BeNull("the budget is spent");
    }
}

/// <summary>Gap E: an own release needs all three legs.</summary>
public class OwnReleaseCheckTests
{
    private const string Producer = "Repositories/github/42";

    private static RepositoryManifest Manifest(string name, string ecosystem = "npm")
        => new() { Repository = Producer, Produces = [new ManifestPackage { Ecosystem = ecosystem, Name = name }] };

    private static readonly Git.Dependencies.LookupDocument Lookup = DependencyTestKit.Lookup(
        DependencyTestKit.Record("@acme/lib", "1.0.0", DependencyTestKit.Candidate(Git.Dependencies.EUpdateType.Minor, "1.1.0", released: "2026-10-16T10:00:00.000Z")));

    [Fact]
    public void A_producer_on_the_account_that_declares_the_package_and_released_at_the_publish_time_is_own()
    {
        var events = new List<DependencyProducerEvent> { new() { ProducerRepository = Producer, Kind = "release", AtUtc = new DateTime(2026, 10, 16, 9, 30, 0, DateTimeKind.Utc) } };

        var own = OwnReleaseCheck.Confirm(Lookup, [Manifest("@acme/lib")], events, DependencyTestKit.Now);

        own.Should().ContainSingle().Which.Version.Should().Be("1.1.0");
    }

    [Fact]
    public void A_look_alike_without_a_release_event_near_the_publish_time_is_not_own()
    {
        var events = new List<DependencyProducerEvent> { new() { ProducerRepository = Producer, Kind = "release", AtUtc = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc) } };

        OwnReleaseCheck.Confirm(Lookup, [Manifest("@acme/lib")], events, DependencyTestKit.Now).Should().BeEmpty();
    }

    [Fact]
    public void A_package_no_repository_of_the_account_declares_is_not_own()
    {
        var events = new List<DependencyProducerEvent> { new() { ProducerRepository = Producer, Kind = "release", AtUtc = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc) } };

        OwnReleaseCheck.Confirm(Lookup, [Manifest("@acme/other")], events, DependencyTestKit.Now).Should().BeEmpty();
        OwnReleaseCheck.Confirm(Lookup, [Manifest("@acme/lib", "nuget")], events, DependencyTestKit.Now).Should().BeEmpty("the ecosystem must match too");
    }
}

/// <summary>R12 / T5: the workflows the bot dispatches, the shipped templates included.</summary>
public class DependencyWorkflowGuardTests
{
    private const string Safe = "on: workflow_dispatch\npermissions:\n  contents: read\njobs:\n  a:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n";

    [Theory]
    [InlineData("dependency-lookup.yml", true)]
    [InlineData("dependency-apply.yml", true)]
    [InlineData("nx-migrate.yml", false)]
    [InlineData("angular-migrate.yml", false)]
    public void The_shipped_templates_pass(string template, bool allowSecrets)
        => DependencyWorkflowGuard.Refusal(DependencyOnboarding.ReadTemplate(template), allowSecrets).Should().BeNull();

    [Theory]
    [InlineData("on: workflow_dispatch\njobs: {}\n", "permissions")]
    [InlineData("on: workflow_dispatch\npermissions: write-all\njobs: {}\n", "write")]
    [InlineData("on: workflow_dispatch\npermissions:\n  contents: write\njobs: {}\n", "write")]
    [InlineData("on: workflow_dispatch\npermissions:\n  contents: read\n  id-token: write\njobs: {}\n", "id-token")]
    [InlineData("on: [pull_request_target]\npermissions:\n  contents: read\njobs: {}\n", "pull_request_target")]
    [InlineData("on: workflow_dispatch\npermissions:\n  contents: read\njobs:\n  a:\n    permissions:\n      pull-requests: write\n    runs-on: x\n", "job")]
    public void An_unsafe_workflow_is_refused(string text, string why)
        => DependencyWorkflowGuard.Refusal(text, allowSecrets: true).Should().Contain(why);

    [Fact]
    public void A_migrate_workflow_may_not_read_secrets()
    {
        var withSecret = Safe.Replace("echo", "echo ${{ secrets.NPM_TOKEN }}");
        DependencyWorkflowGuard.Refusal(withSecret, allowSecrets: false).Should().Contain("secrets");
        DependencyWorkflowGuard.Refusal(withSecret, allowSecrets: true).Should().BeNull("lookup and apply may read feed secrets (P2, D-S14b)");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml", "on: [push, pull_request]\njobs: {}\n", true)]
    [InlineData(".github/workflows/release.yml", "on:\n  push:\n    tags: ['v*']\njobs: {}\n", false)]
    [InlineData("tools/action/action.yml", null, true)]
    public void R15_a_uses_edit_is_tested_only_by_a_workflow_that_runs_on_pull_request(string path, string? text, bool tested)
        => DependencyWorkflowGuard.RunsOnPullRequest(path, text).Should().Be(tested);
}

/// <summary>R16: the onboarding pull request's content, on this repository's real #501 dependabot.yml.</summary>
public class OnboardingPrTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static string RealDependabot() => File.ReadAllText(Path.Combine(DependencyTestKit.RepositoryRoot(), ".github", "dependabot.yml"));

    [Fact]
    public void This_repositorys_dependabot_yml_loses_every_version_update_and_is_deleted()
    {
        var (yml, removed, rules, _) = DependencyOnboarding.TranslateDependabot(RealDependabot());

        yml.Should().BeNull("nothing but the updates and their group remains");
        removed.Should().HaveCount(4);
        removed.Should().Contain(r => r.StartsWith("npm (/, /apps/CodeCoverage/action)"));
        rules.Should().Contain(r => r.Contains("\"@angular/*\": minor"));
        rules.Should().Contain(r => r.Contains("\"ng-packagr\": minor"));
    }

    [Fact]
    public void A_dependabot_yml_with_other_content_keeps_it()
    {
        var (yml, removed, _, _) = DependencyOnboarding.TranslateDependabot("version: 2\nenable-beta-ecosystems: true\nupdates:\n  - package-ecosystem: npm\n    directory: /\n");

        removed.Should().ContainSingle();
        yml.Should().Contain("enable-beta-ecosystems").And.NotContain("updates");
    }

    [Fact]
    public void Ignore_rules_translate_to_coverage_yml_rules()
    {
        var (_, _, rules, _) = DependencyOnboarding.TranslateDependabot("""
            version: 2
            updates:
              - package-ecosystem: npm
                directory: /
                ignore:
                  - dependency-name: left-pad
                  - dependency-name: react
                    update-types: ["version-update:semver-major", "version-update:semver-minor"]
                  - dependency-name: express
                    versions: ["5.x"]
            """);

        var yaml = DependencyOnboarding.RulesYaml(rules);
        yaml.Should().Contain("- { match: [\"left-pad\"], ignore: true }");
        yaml.Should().Contain("\"react\": patch");
        yaml.Should().Contain("# express:");
        CodeCoverage.Dependencies.DependenciesYml.Parse(yaml).Errors.Should().BeEmpty("the block pastes into coverage.yml as it is");
    }

    [Fact]
    public void The_templates_call_the_action_pinned_by_the_resolved_sha_with_the_tag_as_a_comment()
    {
        var plan = DependencyOnboarding.Plan(["package.json"], null, null, null, Sha, DependencyOnboarding.ReadTemplate);

        plan.Pinned.Should().BeTrue();
        var lookup = plan.Files.Single(f => f.Path == ".github/workflows/dependency-lookup.yml").Content!;
        lookup.Should().Contain($"dependency-action@{Sha} # dependency-update-v1").And.NotContain("@dependency-update-v1\n");
        plan.Files.Should().NotContain(f => f.Path.EndsWith("migrate.yml"), "no angular.json or nx.json in the tree");
        plan.Files.Single(f => f.Path == "coverage.yml").Content.Should().StartWith("# MintPlayer dependency updates");
        CodeCoverage.Dependencies.DependenciesYml.Parse(plan.Files.Single(f => f.Path == "coverage.yml").Content).Section.Present.Should().BeFalse("the section is all commented");
    }

    [Fact]
    public void An_unresolved_tag_keeps_the_tag()
    {
        var plan = DependencyOnboarding.Plan(["nx.json", "apps/web/angular.json"], "mergeQueue:\n  enabled: true", null, null, null, DependencyOnboarding.ReadTemplate);

        plan.Pinned.Should().BeFalse();
        plan.Files.Should().Contain(f => f.Path == ".github/workflows/nx-migrate.yml");
        plan.Files.Should().Contain(f => f.Path == ".github/workflows/angular-migrate.yml");
        plan.Files.Single(f => f.Path == ".github/workflows/dependency-apply.yml").Content.Should().Contain(DependencyOnboarding.ActionReference);
        plan.Files.Single(f => f.Path == "coverage.yml").Content.Should().StartWith("mergeQueue:\n  enabled: true\n");
    }
}

/// <summary>The rolling pull request's body: approve hints, holds, markers, the bisect banner.</summary>
public class DependencyPullRequestBodyTests
{
    private static DependencyReportEntry Awaiting(string group, string dep) => new(group, "npm", dep, "package.json", "21.0.0", "22.0.0", EUpdateLevel.Major,
        EDependencyReportReason.AwaitingApproval, "major update over the threshold");

    [Fact]
    public void Awaiting_approval_lists_the_command_to_type_and_held_lists_the_reason()
    {
        var report = new DependencyRunReport
        {
            Groups = [new DependencyReportGroup("chalk", EUpdateLevel.Patch, true, false, false, null, false, [new DependencyReportItem("chalk", "package.json", "npm", "npm", "4.1.0", "4.1.2", "4.1.2", null)])],
            AwaitingApproval = [Awaiting("angular", "@angular/core")],
            Held = [new DependencyReportEntry("lodash", "npm", "lodash", "package.json", "4.16.0", "4.17.21", EUpdateLevel.Minor, EDependencyReportReason.Held, "held by @someone (comment)")],
        };

        var body = DependencyPullRequestBody.Render(new DependencyUpdateRun(), report, null);

        body.Should().StartWith(DependencyPullRequestBody.Marker);
        body.Should().Contain("`approve angular`");
        body.Should().Contain("held by @\u200bsomeone (comment)", "a mention in a reason never pings");
        body.Should().Contain("| chalk | `chalk` | `package.json` | 4.1.0 | 4.1.2 | patch |");
    }

    [Fact]
    public void A_bisection_shows_its_banner_and_a_migration_its_marker()
    {
        var run = new DependencyUpdateRun
        {
            State = EDependencyRunState.Bisecting, MigratedGroups = ["nx"],
            Bisect = new DependencyBisect { Suspects = ["a", "b"], Excluded = ["a"], Spent = 2, Budget = 4 },
        };

        var body = DependencyPullRequestBody.Render(run, new DependencyRunReport(), "It contains code migrations, which never auto-merge.");

        body.Should().Contain("Bisecting (step 2 of 4): don't merge.").And.Contain("Contains code migrations").And.Contain("Not auto-merged: It contains code migrations");
    }

    [Fact]
    public void The_title_names_a_single_group_or_counts_them()
    {
        var one = new DependencyRunReport { Groups = [new DependencyReportGroup("chalk", EUpdateLevel.Patch, true, false, false, null, false, [])] };
        DependencyPullRequestBody.Title(new DependencyUpdateRun(), one).Should().Be("Update chalk");
        DependencyPullRequestBody.Title(new DependencyUpdateRun { Trigger = EDependencyRunTrigger.Security }, one with { Groups = [.. one.Groups, .. one.Groups] })
            .Should().Be("Update dependencies (2 groups), with security fixes");
    }
}
