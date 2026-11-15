using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// prerelease-track-scenarios-PRD §3-§4: a package on a prerelease, against the platform's state, the project's shape and the
/// package's track. The owner's rule (2026-11-15): "if a release within the channel is available, and the library itself also
/// is shipped as stable, it should pick that one", unless an explicit <c>track: preview</c> says otherwise.
/// </summary>
public class PrereleaseScenarioTests
{
    private static readonly DateTimeOffset Later = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Web = "src/Web/Web.csproj";
    private const string Di = "Microsoft.Extensions.DependencyInjection";
    private const string Hosting = "Microsoft.Extensions.Hosting";
    private const string Rc1 = "11.0.0-rc.1.26425.128";

    private static PlatformCandidate Row(string channel, string version, string sdk, bool preview = false)
        => new() { Channel = channel, LatestVersion = version, LatestSdk = sdk, ReleaseTimestamp = "2026-11-10", Preview = preview, Lts = false };

    /// <summary>The 11.0 channel, released (<paramref name="preview"/> false) or still on its RC, with the given projects.</summary>
    private static PlatformRecord Dotnet(bool preview, IEnumerable<(string File, string Tfm)> projects, params PlatformCandidate[] candidates) => new()
    {
        Sdk = "dotnet",
        Source = "releases-index",
        Current = new PlatformCurrent { Channels = ["11.0"], Preview = preview },
        Locations =
        [
            new PlatformLocation { File = "global.json", Kind = "global.json", Value = preview ? "11.0.100-rc.1.26425.128" : "11.0.100", Channel = "11.0" },
            .. projects.Select(p => new PlatformLocation { File = p.File, Kind = "tfm", Value = p.Tfm, Channel = "11.0" }),
        ],
        Candidates = [.. candidates],
    };

    private static PlatformRecord Released(params (string File, string Tfm)[] projects) => Dotnet(false, projects);

    /// <summary>A NuGet record; each candidate labelled as Renovate does: another major is a major, another minor a minor.</summary>
    private static LookupRecord Nuget(string depName, string current, string file = Web, params string[] versions)
        => Record(depName, current, [.. versions.Select(v => Candidate(Label(current, v), v))])
            with { Ecosystem = "nuget", Manager = "nuget", Datasource = "nuget", PackageFile = file, LockFiles = [] };

    private static EUpdateType Label(string current, string version)
    {
        var (from, to) = (current.Split('-')[0].Split('.'), version.Split('-')[0].Split('.'));
        return from[0] != to[0] ? EUpdateType.Major : from[1] != to[1] ? EUpdateType.Minor : EUpdateType.Patch;
    }

    private static DependencyPolicyOutcome Run(PlatformRecord platform, string? yml, params LookupRecord[] records)
        => Evaluate(Lookup(records) with { Platforms = [platform] }, Config(yml), now: Later);

    private static string Track(string dep, string track) => $"dependencies:\n  rules:\n    - match: [\"{dep}\"]\n      track: {track}\n";

    private static IEnumerable<string> Moves(DependencyPolicyOutcome outcome, string dep)
        => outcome.Groups.SelectMany(g => g.Items).Where(i => i.Record.DepName == dep).Select(i => i.ToPlanItem().NewValue!);

    /// <summary>T1 (default track): the rc rides to the release even when a newer prerelease exists (owner's rule, H2).</summary>
    [Fact]
    public void T1_without_a_rule_the_rc_takes_the_release_over_a_newer_prerelease()
    {
        var outcome = Run(Released((Web, "net11.0")), null, Nuget(Di, Rc1, Web, "11.0.0", "11.1.0-preview.1"));

        Moves(outcome, Di).Should().Equal("11.0.0");
    }

    /// <summary>T2: an explicit <c>track: preview</c> keeps the package on prereleases: the newest version on the channel's
    /// major, prereleases included, not the release H2 would force.</summary>
    [Fact]
    public void T2_an_explicit_preview_track_takes_the_newest_prerelease_on_the_major()
    {
        var outcome = Run(Released((Web, "net11.0")), Track(Di, "preview"), Nuget(Di, Rc1, Web, "11.0.0", "11.1.0-preview.1"));

        Moves(outcome, Di).Should().Equal("11.1.0-preview.1");
    }

    /// <summary>T3: an rc with no release yet follows its prereleases (the default track of a package on a prerelease).</summary>
    [Fact]
    public void T3_an_rc_without_a_release_follows_its_prereleases()
    {
        var outcome = Run(Released((Web, "net11.0")), null, Nuget(Di, "11.0.0-rc.1", Web, "11.0.0-rc.2"));

        Moves(outcome, Di).Should().Equal("11.0.0-rc.2");
    }

    /// <summary>T4: <c>track: stable</c> on an rc without a release offers nothing, and says why.</summary>
    [Fact]
    public void T4_a_stable_track_on_an_rc_without_a_release_moves_nothing_and_says_why()
    {
        var outcome = Run(Released((Web, "net11.0")), Track(Di, "stable"), Nuget(Di, "11.0.0-rc.1", Web, "11.0.0-rc.2"));

        Moves(outcome, Di).Should().BeEmpty();
        outcome.Available.Should().Contain(e => e.DepName == Di && e.Reason == EDependencyReportReason.PreviewNotFollowed);
    }

    /// <summary>T5 (CPM): a Directory.Packages.props whose every project targets only the channel aligns like a project:
    /// the .NET 10 package and the rc both take 11.0.0, in one platform group (else NU1605 comes back under CPM).</summary>
    [Fact]
    public void T5_a_central_file_over_net11_projects_aligns_its_rc_and_older_packages_together()
    {
        const string props = "Directory.Packages.props";
        var outcome = Run(Released((Web, "net11.0"), ("src/Api/Api.csproj", "net11.0")), null,
            Nuget(Hosting, "10.0.5", props, "10.0.15", "11.0.0"),
            Nuget(Di, Rc1, props, "11.0.0"));

        outcome.Groups.Should().ContainSingle().Which.Group.Should().Be("platform:dotnet 11.0");
        Moves(outcome, Hosting).Should().Equal("11.0.0");
        Moves(outcome, Di).Should().Equal("11.0.0");
    }

    /// <summary>T6: a central file with a project still on net10.0 under it builds for both channels, so it is split per TFM
    /// (multi-target-dependency-split-PRD P6), not aligned to 11.0.0 for every project.</summary>
    [Fact]
    public void T6_a_central_file_over_a_net10_project_is_split_not_aligned()
    {
        const string props = "Directory.Packages.props";
        var outcome = Run(Released((Web, "net11.0"), ("src/Old/Old.csproj", "net10.0")), null,
            Nuget(Hosting, "10.0.5", props, "10.0.15", "11.0.0"));

        var item = outcome.Groups.SelectMany(g => g.Items).Single(i => i.Record.DepName == Hosting);
        string.Join(", ", item.Layout!.Select(l => $"{l.TargetFramework}={l.Version}")).Should().Be("net10.0=10.0.15, net11.0=11.0.0");
    }

    /// <summary>T7: a tool manifest's rc tool rides to the release (H2 covers what runs on the SDK).</summary>
    [Fact]
    public void T7_a_tool_manifests_rc_tool_rides_to_the_release()
    {
        var outcome = Run(Released((Web, "net11.0")), null, Nuget("dotnet-ef", Rc1, ".config/dotnet-tools.json", "11.0.0"));

        Moves(outcome, "dotnet-ef").Should().Equal("11.0.0");
    }

    /// <summary>T8 (#617 D33 with an explicit track): moving the SDK from its RC to the release, a companion with
    /// <c>track: preview</c> takes the newest version on the major, prereleases included.</summary>
    [Fact]
    public void T8_on_the_release_move_a_preview_track_companion_keeps_prereleases()
    {
        var platform = Dotnet(true, [(Web, "net11.0")], Row("11.0", "11.0.0", "11.0.100"));

        var outcome = Run(platform, Track(Di, "preview"), Nuget(Di, Rc1, Web, "11.0.0", "11.1.0-preview.1"));

        Moves(outcome, Di).Should().Equal("11.1.0-preview.1");
    }

    /// <summary>T9: the same move without a rule rides the companion to the release (D33, owner's rule).</summary>
    [Fact]
    public void T9_on_the_release_move_a_default_companion_rides_to_the_release()
    {
        var platform = Dotnet(true, [(Web, "net11.0")], Row("11.0", "11.0.0", "11.0.100"));

        var outcome = Run(platform, null, Nuget(Di, Rc1, Web, "11.0.0", "11.1.0-preview.1"));

        Moves(outcome, Di).Should().Equal("11.0.0");
    }

    /// <summary>T10: a multi-target project's rc on the net11.0 TFM rides to the release in its split.</summary>
    [Fact]
    public void T10_a_multi_target_projects_rc_takes_the_release_on_its_TFM()
    {
        var hasher = "src/Hasher/Hasher.csproj";
        var outcome = Run(Released((hasher, "net10.0;net11.0")), null,
            Nuget(Di, "10.0.5", hasher, "10.0.15", "11.0.0") with { Condition = "'$(TargetFramework)' == 'net10.0'", TargetFrameworks = ["net10.0"] },
            Nuget(Di, Rc1, hasher, "11.0.0", "11.1.0-preview.1") with { Condition = "'$(TargetFramework)' == 'net11.0'", TargetFrameworks = ["net11.0"] });

        var item = outcome.Groups.SelectMany(g => g.Items).Single(i => i.Record.DepName == Di);
        string.Join(", ", item.Layout!.Select(l => $"{l.TargetFramework}={l.Version}")).Should().Be("net10.0=10.0.15, net11.0=11.0.0");
    }

    /// <summary>T11: a multi-target project's TFM a rule put on <c>track: preview</c> keeps the newest prerelease.</summary>
    [Fact]
    public void T11_a_multi_target_projects_preview_track_TFM_keeps_prereleases()
    {
        var hasher = "src/Hasher/Hasher.csproj";
        var outcome = Run(Released((hasher, "net10.0;net11.0")), Track(Di, "preview"),
            Nuget(Di, "10.0.5", hasher, "10.0.15", "11.0.0") with { Condition = "'$(TargetFramework)' == 'net10.0'", TargetFrameworks = ["net10.0"] },
            Nuget(Di, Rc1, hasher, "11.0.0", "11.1.0-preview.1") with { Condition = "'$(TargetFramework)' == 'net11.0'", TargetFrameworks = ["net11.0"] });

        var item = outcome.Groups.SelectMany(g => g.Items).Single(i => i.Record.DepName == Di);
        string.Join(", ", item.Layout!.Select(l => $"{l.TargetFramework}={l.Version}")).Should().Be("net10.0=10.0.15, net11.0=11.1.0-preview.1");
    }

    // ═══ D5: the bot's hold on a platform group binds to the items it refused ═════════════════════════════════════════

    private const string PlatformGroup = "platform:dotnet 11.0";

    /// <summary>Tools #218 after #679: Hosting 10.0.5 and DI rc.1 in a net11.0 project, on the 11.0.0 → 11.0.1 patch.</summary>
    private static DependencyPolicyOutcome RunHeld(params string[] refused)
    {
        var platform = Dotnet(false, [(Web, "net11.0")], Row("11.0", "11.0.1", "11.0.101"));
        var state = new DependencyUpdateState
        {
            Repository = "Repositories/github/1",
            Holds =
            [
                new DependencyHold
                {
                    Id = $"blocker:{PlatformGroup}", Kind = EDependencyHoldKind.Blocker, Group = PlatformGroup, DepNames = ["platform:dotnet"],
                    Reason = $"the resolver refused it: blocked by {Di} (R2)",
                    Blockers = [new DependencyBlockerSeen { DepName = Di, SeenVersions = [Rc1, "11.0.0"], CurrentTokens = [Rc1] }],
                    Refused = [.. refused],
                },
            ],
        };
        return Evaluate(Lookup(Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0"), Nuget(Di, Rc1, Web, "11.0.0")) with { Platforms = [platform] },
            Config(null), state, now: Later);
    }

    /// <summary>T12: the refused plan moved Hosting alone; the plan now carries DI along, so the hold lifts.</summary>
    [Fact]
    public void T12_a_platform_hold_lifts_when_the_group_is_planned_with_other_items()
    {
        var outcome = RunHeld($"{Hosting}|{Web}|11.0.0");

        outcome.Groups.Should().ContainSingle(g => g.Group == PlatformGroup);
        outcome.NextState.Holds.Should().NotContain(h => h.Group == PlatformGroup);
        outcome.LiftedHolds.Should().Contain(h => h.Group == PlatformGroup);
    }

    /// <summary>T13: the same items the resolver refused stay held.</summary>
    [Fact]
    public void T13_a_platform_hold_stands_for_the_items_it_refused()
    {
        var outcome = RunHeld($"{Di}|{Web}|11.0.0", $"{Hosting}|{Web}|11.0.0");

        outcome.Groups.Should().NotContain(g => g.Group == PlatformGroup);
        outcome.Held.Should().Contain(e => e.Group == PlatformGroup);
        outcome.NextState.Holds.Should().Contain(h => h.Group == PlatformGroup);
    }

    /// <summary>T15: the apply's refusal of a platform group records its items, and the same plan then stays held (no retry
    /// loop: the lookup is unchanged, so the engine plans the same group).</summary>
    [Fact]
    public void T15_a_refused_platform_group_records_its_items_and_the_same_plan_stays_held()
    {
        var platform = Dotnet(false, [(Web, "net11.0")], Row("11.0", "11.0.1", "11.0.101"));
        var lookup = Lookup(Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0"), Nuget(Di, Rc1, Web, "11.0.0")) with { Platforms = [platform] };
        var first = Evaluate(lookup, Config(null), now: Later);
        var planned = first.Groups.Single(g => g.Group == PlatformGroup);
        var state = first.NextState;
        var plan = new DependencyPlan
        {
            PlanId = "p", ExpectedSha = Sha,
            Groups = [new PlanGroup { Group = planned.Group, Items = [.. planned.Items.Select(i => i.ToPlanItem())] }],
        };
        var result = new ApplyResult
        {
            PlanId = "p", ExpectedSha = Sha,
            Groups = [new ApplyGroupResult
            {
                Group = PlatformGroup, Status = EApplyGroupStatus.Unresolvable,
                Blockers = [new ApplyBlocker { DepName = "Microsoft.Extensions.Logging", Version = "11.0.0", Code = "NU1605", Message = "Detected package downgrade" }],
            }],
        };

        DependencyHolds.RecordApplyResult(state, plan, result, lookup, Later).Should().ContainSingle().Which.Refused
            .Should().Equal($"{Di}|{Web}|11.0.0", $"{Hosting}|{Web}|11.0.0");
        var second = Evaluate(lookup, Config(null), state, now: Later);

        second.Groups.Should().NotContain(g => g.Group == PlatformGroup);
        second.NextState.Holds.Should().ContainSingle(h => h.Group == PlatformGroup);
    }

    /// <summary>T14: a hold taken before it recorded its items lifts once; the next apply records them.</summary>
    [Fact]
    public void T14_a_platform_hold_without_its_items_lifts_once()
    {
        var outcome = RunHeld();

        outcome.Groups.Should().ContainSingle(g => g.Group == PlatformGroup);
    }
}
