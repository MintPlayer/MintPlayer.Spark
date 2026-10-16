using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using CodeCoverage.Tests.Dependencies.Policy;
using Xunit;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>D-N5: the fleet view, the repository status and readiness per platform major, from the saga's own state.</summary>
public class DependencyFleetViewTests
{
    private static Repository Repo(long id, string name) => new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, id), GitHubId = id, Name = name, FullName = $"acme/{name}", OwnerLogin = "acme", DefaultBranch = "main",
    };

    private static RepositoryManifest Uses(params (string Name, string Version)[] packages) => new()
    {
        LookupAt = DependencyTestKit.Now,
        LookupConsumes = [.. packages.Select(p => new ManifestDependency { Ecosystem = "npm", Name = p.Name, Path = "package.json", Version = p.Version, Constraint = "^" + p.Version })],
    };

    private static DependencyReportEntry Entry(string depName, string current, string target, EDependencyReportReason reason, string group = "@angular/*")
        => new(group, "npm", depName, "package.json", current, target, EUpdateLevel.Major, reason, reason.ToString());

    [Theory]
    [InlineData(false, null, "off")]
    [InlineData(true, null, "new")]
    [InlineData(true, "red", "red")]
    [InlineData(true, "bisected", "red")]
    [InlineData(true, "replay-conflict", "frozen")]
    [InlineData(true, "dispatch-uncorrelated", "silent")]
    [InlineData(true, "lookup-timeout", "silent")]
    [InlineData(true, "apply-missing", "silent")]
    [InlineData(true, "patch-rejected", "failed")]
    [InlineData(true, "green", "ok")]
    public void The_health_word_says_what_a_reader_needs_first(bool enabled, string? outcome, string health)
    {
        var run = outcome is null && enabled ? null : new DependencyUpdateRun { Outcome = outcome, Onboarding = EDependencyOnboarding.Done };
        DependencyFleetView.Health(enabled, run).Should().Be(health);
    }

    [Fact]
    public void A_running_bisecting_or_blocked_run_says_so_before_its_last_outcome()
    {
        DependencyFleetView.Health(true, new DependencyUpdateRun { State = EDependencyRunState.Bisecting, Outcome = "green" }).Should().Be("bisecting");
        DependencyFleetView.Health(true, new DependencyUpdateRun { State = EDependencyRunState.Applying, Outcome = "red" }).Should().Be("running");
        DependencyFleetView.Health(true, new DependencyUpdateRun { State = EDependencyRunState.AwaitingChecks }).Should().Be("waiting");
        DependencyFleetView.Health(true, new DependencyUpdateRun { Onboarding = EDependencyOnboarding.Blocked, Outcome = "green" }).Should().Be("blocked");
    }

    [Fact]
    public void Readiness_counts_who_can_move_to_the_next_platform_major_and_names_the_blockers()
    {
        var app = Repo(1, "app");
        var web = Repo(2, "web");
        var lib = Repo(3, "lib");
        var input = new DependencyFleetInput
        {
            Repositories = [app, web, lib],
            Account = new Account { DependencyUpdates = true },
            Now = DependencyTestKit.Now,
            Manifests = new Dictionary<string, RepositoryManifest>
            {
                [app.Id!] = Uses(("@angular/core", "21.2.0")),
                [web.Id!] = Uses(("@angular/core", "21.1.0")),
                [lib.Id!] = Uses(("@angular/core", "22.0.1")),
            },
            Reports = new Dictionary<string, DependencyRunReport>
            {
                [app.Id!] = new() { AwaitingApproval = [Entry("@angular/core", "21.2.0", "22.0.1", EDependencyReportReason.AwaitingApproval)] },
                [web.Id!] = new() { Held = [Entry("@angular/core", "21.1.0", "22.0.1", EDependencyReportReason.Held)] },
            },
            States = new Dictionary<string, DependencyUpdateState>
            {
                [web.Id!] = new()
                {
                    Holds = [new DependencyHold
                    {
                        Id = "blocker:@angular/*", Kind = EDependencyHoldKind.Blocker, Group = "@angular/*", DepNames = ["@angular/core"],
                        Reason = "the resolver refused it", Blockers = [new DependencyBlockerSeen { DepName = "@nx/angular" }],
                    }],
                },
            },
        };

        var readiness = DependencyFleetView.Build(input).Readiness.Should().ContainSingle().Which;

        readiness.Platform.Should().Be("Angular");
        readiness.NextMajor.Should().Be("22");
        readiness.Ready.Should().Be(2);
        readiness.Total.Should().Be(3);
        readiness.Repositories.Select(r => $"{r.FullName}: {r.Status}").Should().Equal(["acme/app: ready", "acme/lib: on-target", "acme/web: blocked"]);
        readiness.Repositories.Single(r => r.FullName == "acme/web").Blockers.Should().Equal(["@nx/angular"]);
        readiness.Blockers.Should().ContainSingle().Which.Should().Be(new DependencyBlockerCount("@nx/angular", 1));
    }

    [Fact]
    public void A_platform_everyone_is_on_is_not_listed()
    {
        var a = Repo(1, "a");
        var input = new DependencyFleetInput
        {
            Repositories = [a], Now = DependencyTestKit.Now,
            Manifests = new Dictionary<string, RepositoryManifest> { [a.Id!] = Uses(("@angular/core", "22.0.1")) },
        };

        DependencyFleetView.Build(input).Readiness.Should().BeEmpty();
    }

    [Fact]
    public void A_package_row_lists_each_repositorys_version_and_what_is_going_on_with_it()
    {
        var app = Repo(1, "app");
        var web = Repo(2, "web");
        var solo = Repo(3, "solo");
        var input = new DependencyFleetInput
        {
            Repositories = [app, web, solo],
            Now = DependencyTestKit.Now,
            Manifests = new Dictionary<string, RepositoryManifest>
            {
                [app.Id!] = Uses(("chalk", "4.1.0")),
                [web.Id!] = Uses(("chalk", "4.1.2")),
                [solo.Id!] = Uses(("left-pad", "1.0.0")),
            },
            Reports = new Dictionary<string, DependencyRunReport>
            {
                [app.Id!] = new() { Groups = [new DependencyReportGroup("chalk", EUpdateLevel.Patch, true, false, false, null, false,
                    [new DependencyReportItem("chalk", "package.json", "npm", "npm", "4.1.0", "4.1.2", "4.1.2", null)])] },
            },
        };

        var packages = DependencyFleetView.Build(input).Packages;

        var row = packages.Should().ContainSingle("a package one repository uses with nothing going on is no row").Which;
        row.Name.Should().Be("chalk");
        row.Uses.Select(u => $"{u.FullName} {u.Version} {u.Status}").Should().Equal(["acme/app 4.1.0 in the pull request → 4.1.2", "acme/web 4.1.2 "]);
    }

    [Fact]
    public void The_next_slot_is_the_next_window_the_cron_would_find_due()
    {
        var schedule = new DependencySchedule(EDependencyScheduleFrequency.Weekly, EDependencyWeekday.Monday, 6, "UTC");
        const string id = "Repositories/github/910040";
        var minute = DependencySlots.SpreadMinute(id);
        var friday = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var monday = new DateTimeOffset(2026, 10, 19, 6, minute, 0, TimeSpan.Zero);

        DependencySlots.Next(schedule, friday, id).Should().Be(monday);
        DependencySlots.Due(schedule, monday, id).Due.Should().BeTrue();
        DependencySlots.Next(schedule, monday.AddSeconds(30), id).Should().Be(monday.AddSeconds(30), "a window that is open is due now");
        DependencySlots.Next(schedule, new DateTimeOffset(2026, 10, 19, 7, 0, 0, TimeSpan.Zero), id).Should().Be(monday.AddDays(7));
    }

    [Fact]
    public void The_status_names_the_rolling_pull_request_and_the_slot_after_one_that_already_ran()
    {
        var repo = Repo(910040, "widget");
        var schedule = new DependencySchedule(EDependencyScheduleFrequency.Weekly, EDependencyWeekday.Monday, 6, "UTC");
        var opened = new DateTimeOffset(2026, 10, 19, 6, DependencySlots.SpreadMinute(repo.Id!), 0, TimeSpan.Zero);
        var input = new DependencyFleetInput
        {
            Repositories = [repo], Account = new Account { DependencyUpdates = true }, Now = opened.AddMinutes(2),
            Runs = new Dictionary<string, DependencyUpdateRun>
            {
                [repo.Id!] = new() { Trigger = EDependencyRunTrigger.Scheduled, StartedAtUtc = opened.UtcDateTime, PullRequestNumber = 41, PullRequestIsDraft = true, State = EDependencyRunState.AwaitingChecks },
            },
        };

        var status = DependencyFleetView.Status(input, repo);

        status.PullRequest.Should().Be(new DependencyPullRequestLink(41, "https://github.com/acme/widget/pull/41", true));
        status.NextSlotUtc.Should().Be(opened.AddDays(7), "this week's slot already started its run");
        status.Schedule.Should().Be("Mondays at 06:00 UTC");
        schedule.Hour.Should().Be(6);
    }
}
