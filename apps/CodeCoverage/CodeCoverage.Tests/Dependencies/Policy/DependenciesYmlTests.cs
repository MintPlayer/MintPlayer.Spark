using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.LookupReferences;
using Xunit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary><c>coverage.yml</c>'s <c>dependencies:</c> section (PRD §4.3, R6, R8, R9, D-S12e, gap B) and the owner settings'
/// cascade (§4.1).</summary>
public class DependenciesYmlTests
{
    private const string Full = """
        dependencies:
          enabled: true
          schedule: { frequency: daily, weekday: friday, hour: 4 }
          cooldown: 7d
          autoMerge:
            default: patch
            "@vitest/*": minor
            vitest: major
            maxCoverageDrop: 0.5
          ignore: ["Microsoft.CodeAnalysis*"]
          rules:
            - match: ["typescript"]
              ecosystem: npm
              allowMajor: true
              group: ts
            - match: left-pad
              hold: true
            - match: ["chalk"]
              hold: "waiting for ESM"
          exclude: ["samples/**", "**/fixtures/**"]
          lookupAction: lookup.yml
          applyAction: apply.yaml
          migrate:
            - match: "@angular/core"
              onMajor: angular-migrate.yml
          platforms:
            dotnet: { strategy: add, keepPrevious: 1, lockPackageMajor: true, locations: ["Directory.Build.props"], exclude: ["legacy/**"] }
            node: {}
          lockFileMaintenance: true
          registries: ["npm.pkg.github.com", "NUGET.example.com:8443"]
        """;

    private static string RepositoryFile()
        => File.ReadAllText(Path.Combine(DependencyTestKit.RepositoryRoot(), "coverage.yml"));

    [Fact]
    public void This_repositorys_own_file_parses_with_no_error_and_the_built_in_defaults()
    {
        var parsed = DependenciesYml.Parse(RepositoryFile());

        parsed.Errors.Should().BeEmpty();
        var section = parsed.Section;
        section.Enabled.Should().BeTrue("the file may only opt out, and it does not");
        section.DefaultThreshold.Should().Be(EDependencyThreshold.Minor);
        section.LookupAction.Should().Be("dependency-lookup.yml");
        section.ApplyAction.Should().Be("dependency-apply.yml");
        section.LockFileMaintenance.Should().BeFalse("D-S12e: off by default");
        section.MaxCoverageDrop.Should().Be(0);
    }

    [Fact]
    public void No_file_and_no_section_are_the_defaults_without_errors()
    {
        DependenciesYml.Parse(null).Section.Present.Should().BeFalse();
        DependenciesYml.Parse("mergeQueue:\n  enabled: true\n").Errors.Should().BeEmpty();
    }

    [Fact]
    public void Every_documented_key_is_read()
    {
        var parsed = DependenciesYml.Parse(Full);

        parsed.Errors.Should().BeEmpty();
        var s = parsed.Section;
        s.Present.Should().BeTrue();
        s.Schedule.Should().Be(new DependencyScheduleOverride(EDependencyScheduleFrequency.Daily, EDependencyWeekday.Friday, 4));
        s.MinimumCooldown.Should().Be(TimeSpan.FromDays(7));
        s.DefaultThreshold.Should().Be(EDependencyThreshold.Patch);
        s.AutoMerge.Should().Equal([new DependencyThresholdGlob("@vitest/*", EDependencyThreshold.Minor), new DependencyThresholdGlob("vitest", EDependencyThreshold.Major)]);
        s.MaxCoverageDrop.Should().Be(0.5);
        s.Rules.Should().HaveCount(4);
        s.Rules[0].Ignore.Should().BeTrue();
        s.Rules[0].Match.Should().Equal(["Microsoft.CodeAnalysis*"]);
        s.Rules[1].AllowMajor.Should().Be(true);
        s.Rules[1].Group.Should().Be("ts");
        s.Rules[1].Ecosystem.Should().Be("npm");
        s.Rules[2].Hold.Should().Be("held by coverage.yml");
        s.Rules[3].Hold.Should().Be("waiting for ESM");
        s.Exclude.Should().Equal(["samples/**", "**/fixtures/**"]);
        s.LookupAction.Should().Be("lookup.yml");
        s.ApplyAction.Should().Be("apply.yaml");
        s.Migrate.Should().ContainSingle(m => m.OnMajor == "angular-migrate.yml" && m.Match.Single() == "@angular/core");
        var dotnet = s.Platform("dotnet");
        dotnet.Strategy.Should().Be("add");
        dotnet.KeepPrevious.Should().Be(1);
        dotnet.LockPackageMajor.Should().BeTrue();
        dotnet.Locations.Should().Equal(["Directory.Build.props"]);
        dotnet.Exclude.Should().Equal(["legacy/**"]);
        s.Platform("node").Strategy.Should().Be("replace", "R8's default");
        s.Platform("node").LockPackageMajor.Should().BeFalse("R8: an explicit opt-in");
        s.Platform("python").Should().Be(DependencyPlatformSettings.Default);
        s.LockFileMaintenance.Should().BeTrue();
        s.Registries.Should().Equal(["npm.pkg.github.com", "nuget.example.com:8443"]);
    }

    [Theory]
    [InlineData("autoMerge: { default: sometimes }", "autoMerge.default")]
    [InlineData("autoMerge: { \"@x/*\": always }", "autoMerge.@x/*")]
    [InlineData("autoMerge: { maxCoverageDrop: lots }", "maxCoverageDrop")]
    [InlineData("lookupAction: ../evil.yml", "lookupAction")]
    [InlineData("applyAction: apply.sh", "applyAction")]
    [InlineData("migrate: [{ match: x, onMajor: run.sh }]", "onMajor")]
    [InlineData("migrate: [{ onMajor: m.yml }]", "match")]
    [InlineData("platforms: { rust: {} }", "rust")]
    [InlineData("platforms: { dotnet: { strategy: merge } }", "strategy")]
    [InlineData("platforms: { node: { keepPrevious: 9 } }", "keepPrevious")]
    [InlineData("platforms: { node: { locations: [\"../x\"] } }", "../x")]
    [InlineData("registries: [\"https://evil.example.com/\"]", "registries")]
    [InlineData("exclude: [\"/abs/**\"]", "exclude")]
    [InlineData("cooldown: forever", "cooldown")]
    [InlineData("cooldown: 120d", "cooldown")]
    [InlineData("schedule: { hour: 24 }", "hour")]
    [InlineData("schedule: { weekday: someday }", "weekday")]
    [InlineData("schedule: { frequency: hourly }", "frequency")]
    [InlineData("rules: [{ match: [] }]", "match")]
    [InlineData("rules: [{ match: x, ecosystem: \"NPM DROP\" }]", "ecosystem")]
    [InlineData("ignore: [\"\"]", "ignore")]
    public void An_invalid_value_is_an_error_and_the_section_falls_back_to_the_defaults(string body, string mentioned)
    {
        var parsed = DependenciesYml.Parse($"dependencies:\n  {body}\n");

        parsed.Errors.Should().Contain(e => e.Contains(mentioned));
        parsed.Section.Rules.Should().BeEmpty("§4.3: a malformed section falls back to the defaults");
        parsed.Section.AutoMerge.Should().BeEmpty();
        parsed.Section.DefaultThreshold.Should().Be(EDependencyThreshold.Minor);
        parsed.Section.Enabled.Should().BeTrue();
    }

    [Fact]
    public void A_broken_section_still_honours_its_opt_out()
    {
        var parsed = DependenciesYml.Parse("dependencies:\n  enabled: false\n  autoMerge: { default: always }\n");

        parsed.Errors.Should().ContainSingle();
        parsed.Section.Enabled.Should().BeFalse("an opt-out can only make the bot do less");
    }

    [Fact]
    public void Unreadable_yaml_is_an_error_not_an_exception()
    {
        var parsed = DependenciesYml.Parse("dependencies: [unclosed\n  - : :");

        parsed.Errors.Should().ContainSingle(e => e.StartsWith("coverage.yml could not be read"));
        parsed.Section.Enabled.Should().BeTrue();
    }

    [Fact]
    public void The_settings_opt_in_and_the_file_may_only_opt_out()
    {
        var account = new Account { DependencyUpdates = true };
        var inherit = new Repository();
        var off = DependenciesYml.Parse("dependencies:\n  enabled: false\n").Section;
        var on = DependenciesYml.Parse("dependencies:\n  enabled: true\n").Section;

        DependencyPolicyConfig.Enabled(on, inherit, account).Should().BeTrue();
        DependencyPolicyConfig.Enabled(off, inherit, account).Should().BeFalse();
        DependencyPolicyConfig.Enabled(on, inherit, new Account()).Should().BeFalse("off by default (G2), and the file cannot opt in (Q6)");
        DependencyPolicyConfig.Enabled(on, new Repository { DependencyUpdates = EDependencyUpdatesPolicy.Enabled }, new Account()).Should().BeTrue();
        DependencyPolicyConfig.Enabled(on, new Repository { DependencyUpdates = EDependencyUpdatesPolicy.Disabled }, account).Should().BeFalse();
        DependencyPolicyConfig.Enabled(on, inherit, null).Should().BeFalse();
    }

    [Fact]
    public void The_schedule_cascades_repository_then_account_then_default_and_the_file_overrides_its_parts()
    {
        var account = new Account { DependencyScheduleFrequency = EDependencyScheduleFrequency.Daily, DependencyScheduleHour = 22, DependencyTimeZone = "Europe/Brussels" };
        var own = new Repository
        {
            DependencySchedulePolicy = EDependencySchedulePolicy.Override,
            DependencyScheduleFrequency = EDependencyScheduleFrequency.Monthly,
            DependencyScheduleWeekday = EDependencyWeekday.Sunday,
            DependencyScheduleHour = 3,
        };

        Repository.ResolveDependencySchedule(new Repository(), null).Should().Be((new DependencySchedule(EDependencyScheduleFrequency.Weekly, EDependencyWeekday.Monday, 6, "UTC"), "default"));
        Repository.ResolveDependencySchedule(new Repository(), account).Should().Be((new DependencySchedule(EDependencyScheduleFrequency.Daily, EDependencyWeekday.Monday, 22, "Europe/Brussels"), "organization"));
        Repository.ResolveDependencySchedule(own, account).Should().Be((new DependencySchedule(EDependencyScheduleFrequency.Monthly, EDependencyWeekday.Sunday, 3, "Europe/Brussels"), "repository"));
        Repository.ResolveDependencySchedule(own, new Account { DependencyScheduleHour = 99 }).Schedule.Hour.Should().Be(3);
        Repository.ResolveDependencySchedule(new Repository(), new Account { DependencyScheduleHour = 99 }).Schedule.Hour.Should().Be(6, "an hour outside 0-23 is read as the default");

        var file = DependenciesYml.Parse("dependencies:\n  schedule: { hour: 5 }\n").Section;
        DependencyPolicyConfig.Schedule(file, new Repository(), account).Should().Be(new DependencySchedule(EDependencyScheduleFrequency.Daily, EDependencyWeekday.Monday, 5, "Europe/Brussels"));
    }

    [Fact]
    public void An_old_document_reads_the_defaults()
    {
        // The zero values ARE the defaults, and the one integer has an initializer: no migration is needed to READ.
        new Account().DependencyScheduleHour.Should().Be(6);
        new Account().DependencyCooldown.Should().Be(EDependencyCooldown.ThreeDays);
        new Account().DependencyBisectBudget.Should().Be(EDependencyBisectBudget.Two);
        new Account().DependencyScheduleFrequency.Should().Be(EDependencyScheduleFrequency.Weekly);
        new Repository().DependencyUpdates.Should().Be(EDependencyUpdatesPolicy.Inherit);
    }

    [Fact]
    public void Cooldown_and_bisect_budget_cascade_and_the_file_may_only_lengthen_the_cooldown()
    {
        Repository.ResolveDependencyCooldown(new Repository(), null).Should().Be((TimeSpan.FromDays(3), "default"));
        Repository.ResolveDependencyCooldown(new Repository(), new Account { DependencyCooldown = EDependencyCooldown.None }).Should().Be((TimeSpan.Zero, "organization"));
        Repository.ResolveDependencyCooldown(new Repository { DependencyCooldown = EDependencyCooldownPolicy.SevenDays }, new Account()).Should().Be((TimeSpan.FromDays(7), "repository"));
        Repository.ResolveDependencyBisectBudget(new Repository(), null).Should().Be((2, "default"));
        Repository.ResolveDependencyBisectBudget(new Repository { DependencyBisectBudget = EDependencyBisectBudgetPolicy.Off }, new Account()).Should().Be((0, "repository"));
        Repository.ResolveDependencyBisectBudget(new Repository(), new Account { DependencyBisectBudget = EDependencyBisectBudget.Eight }).Should().Be((8, "organization"));

        var longer = DependenciesYml.Parse("dependencies:\n  cooldown: 7d\n").Section;
        var shorter = DependenciesYml.Parse("dependencies:\n  cooldown: 0\n").Section;
        DependencyPolicyConfig.Resolve(longer, new Repository(), new Account()).Cooldown.Should().Be(TimeSpan.FromDays(7));
        DependencyPolicyConfig.Resolve(shorter, new Repository(), new Account()).Cooldown.Should().Be(TimeSpan.FromDays(3), "a file cannot shorten the owner's cooldown");
    }

    [Fact]
    public void The_threshold_is_the_most_specific_file_glob_then_the_built_ins_then_the_ecosystem_then_the_default()
    {
        var config = DependencyTestKit.Config("""
            dependencies:
              autoMerge:
                default: patch
                "@vitest/*": minor
                "@vitest/coverage-v8": major
                "@angular/cdk": minor
            """);

        config.Threshold("npm", "@vitest/coverage-v8").Should().Be(EDependencyThreshold.Major, "the exact name beats the glob");
        config.Threshold("npm", "@vitest/ui").Should().Be(EDependencyThreshold.Minor);
        config.Threshold("npm", "@angular/cdk").Should().Be(EDependencyThreshold.Minor, "a file glob beats the built-in");
        config.Threshold("npm", "@angular/core").Should().Be(EDependencyThreshold.Never, "R6's built-in");
        config.Threshold("github-actions", "actions/checkout").Should().Be(EDependencyThreshold.Patch, "the built-in actions/* is minor, and the file default patch is lower for the rest");
        config.Threshold("github-actions", "docker/login-action").Should().Be(EDependencyThreshold.Patch);
        config.Threshold("npm", "chalk").Should().Be(EDependencyThreshold.Patch);
        DependencyTestKit.Config("dependencies:\n  autoMerge: { default: major }\n").Threshold("github-actions", "docker/login-action")
            .Should().Be(EDependencyThreshold.Minor, "R15: an Actions major stays over the threshold by default");
        DependencyTestKit.Config("dependencies:\n  autoMerge: { default: major }\n").Threshold("platform:node", "platform:node")
            .Should().Be(EDependencyThreshold.Minor, "R7: platform majors never by default");
    }

    [Fact]
    public void An_enforced_account_rule_is_a_ceiling_the_file_cannot_lift_and_a_default_one_is_replaced_by_the_file()
    {
        var config = DependencyTestKit.Config("""
            dependencies:
              autoMerge: { "@types/*": major, "left-pad": major }
              rules:
                - match: "@types/*"
                  allowMajor: true
                - match: "debug"
                  ignore: false
            """,
            DependencyTestKit.AccountRule("@types/*", EDependencyThreshold.Patch),
            DependencyTestKit.AccountRule("left-pad", EDependencyThreshold.Never, enforced: false),
            DependencyTestKit.AccountRule("debug", EDependencyThreshold.Minor, enforced: false, ignore: true),
            DependencyTestKit.AccountRule("rimraf", EDependencyThreshold.Major, enforced: true, ignore: true),
            DependencyTestKit.AccountRule("uuid", EDependencyThreshold.Patch, enforced: false));

        config.Threshold("npm", "@types/node").Should().Be(EDependencyThreshold.Patch);
        config.Inclusion("npm", "@types/node").Should().Be(EDependencyThreshold.Patch, "allowMajor stays under the enforced ceiling");
        config.Ceiling("npm", "@types/node").Should().Be(EDependencyThreshold.Patch);
        config.Threshold("npm", "left-pad").Should().Be(EDependencyThreshold.Major, "the file replaces a default account rule");
        config.IgnoredBy("npm", "debug").Should().BeNull("the file's own rule names it, so the default ignore is replaced");
        config.IgnoredBy("npm", "rimraf").Should().Contain("account rule", "an enforced ignore cannot be undone");
        config.Threshold("npm", "uuid").Should().Be(EDependencyThreshold.Patch, "a default account rule applies where the file is silent");
    }
}
