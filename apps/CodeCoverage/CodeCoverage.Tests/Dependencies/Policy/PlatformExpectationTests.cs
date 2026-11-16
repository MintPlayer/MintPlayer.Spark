using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// prerelease-track-scenarios-PRD §3.5: the owner's expectations of the package updater, one test each (2026-11-15:
/// "packages matching specific globs follow the SDK version, preview-sdk allow preview packages, stable sdk only allows
/// stable packages, unless they're on an exclusion list in the coverage.yml file"), and the documented exceptions to them.
/// The non-platform track rules (a beta package, a preview rule, an enforced stable rule) are PackagePrereleaseTrackTests
/// A1-A5.
/// </summary>
public class PlatformExpectationTests
{
    private static readonly DateTimeOffset Later = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Web = "src/Web/Web.csproj";
    private const string Di = "Microsoft.Extensions.DependencyInjection";
    private const string Hosting = "Microsoft.Extensions.Hosting";
    private const string Json = "Newtonsoft.Json";
    private const string Rc1 = "11.0.0-rc.1.26425.128";
    private const string AllMajor = "dependencies:\n  autoMerge: { default: major }\n";

    private static PlatformCandidate Row(string channel, string version, string sdk, bool preview = false)
        => new() { Channel = channel, LatestVersion = version, LatestSdk = sdk, ReleaseTimestamp = "2026-11-10", Preview = preview, Lts = false };

    /// <summary>The SDK on <paramref name="channel"/> (an RC when <paramref name="preview"/>), with one project.</summary>
    private static PlatformRecord Dotnet(string channel, bool preview, string tfm = "net11.0", string file = Web, params PlatformCandidate[] candidates) => new()
    {
        Sdk = "dotnet",
        Source = "releases-index",
        Current = new PlatformCurrent { Channels = [channel], Preview = preview },
        Locations =
        [
            new PlatformLocation { File = "global.json", Kind = "global.json", Value = preview ? $"{channel}.100-rc.1.26425.128" : $"{channel}.100", Channel = channel },
            new PlatformLocation { File = file, Kind = "tfm", Value = tfm, Channel = tfm.StartsWith("net", StringComparison.Ordinal) && char.IsDigit(tfm[3]) ? tfm[3..] : null },
        ],
        Candidates = [.. candidates],
    };

    private static LookupRecord Nuget(string depName, string current, string file = Web, params string[] versions)
        => Record(depName, current, [.. versions.Select(v => Candidate(Label(current, v), v))])
            with { Ecosystem = "nuget", Manager = "nuget", Datasource = "nuget", PackageFile = file, LockFiles = [] };

    /// <summary>As Renovate labels it: another major is a major, another minor a minor.</summary>
    private static EUpdateType Label(string current, string version)
    {
        var (from, to) = (current.Split('-')[0].Split('.'), version.Split('-')[0].Split('.'));
        return from[0] != to[0] ? EUpdateType.Major : from[1] != to[1] ? EUpdateType.Minor : EUpdateType.Patch;
    }

    private static DependencyPolicyOutcome Run(PlatformRecord platform, string? yml, params LookupRecord[] records)
        => RunWith(platform, Config(yml), records);

    private static DependencyPolicyOutcome RunWith(PlatformRecord platform, DependencyPolicyConfig config, params LookupRecord[] records)
        => Evaluate(Lookup(records) with { Platforms = [platform] }, config, now: Later);

    private static IEnumerable<string> Moves(DependencyPolicyOutcome outcome, string dep)
        => outcome.Groups.SelectMany(g => g.Items).Where(i => i.Record.DepName == dep).Select(i => i.ToPlanItem().NewValue);

    private static string? GroupOf(DependencyPolicyOutcome outcome, string dep)
        => outcome.Groups.FirstOrDefault(g => g.Items.Any(i => i.Record.DepName == dep))?.Group;

    // ═══ Companions follow the SDK ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>E1: on the SDK's major move (a writer's <c>/approve</c>, R7), every companion (the built-in globs) rides to
    /// the new major in the platform group; an ordinary package is not part of it.</summary>
    [Fact]
    public void E1_on_a_major_move_the_companions_ride_to_the_new_major_and_ordinary_packages_do_not()
    {
        var platform = Dotnet("10.0", false, "net10.0", Web, Row("11.0", "11.0.0", "11.0.100"));
        var lookup = Lookup(
            Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0"),
            Nuget(Di, "10.0.5", Web, "10.0.15", "11.0.0"),
            Nuget(Json, "13.0.3", Web, "13.0.4")) with { Platforms = [platform] };
        var waiting = Evaluate(lookup, Config(null), now: Later, configBlob: "b");
        waiting.AwaitingApproval.Should().Contain(e => e.Group == "platform:dotnet 11.0");
        DependencyHolds.Approve(waiting.NextState, waiting, "platform:dotnet 11.0", "b", "alice", 1, Later).Should().NotBeNull();

        var outcome = Evaluate(lookup, Config(null), waiting.NextState, now: Later, configBlob: "b");

        var group = outcome.Groups.Single(g => g.Platform is not null);
        group.Group.Should().Be("platform:dotnet 11.0");
        group.Items.Select(i => $"{i.Record.DepName}@{i.ToPlanItem().NewValue}").Order(StringComparer.Ordinal)
            .Should().Equal($"{Di}@11.0.0", $"{Hosting}@11.0.0");
        GroupOf(outcome, Json).Should().Be(Json);
    }

    /// <summary>E2: a project on the SDK's channel only takes the channel's major for a companion on an older major, without
    /// a move (follower-cooldown-PRD H1).</summary>
    [Fact]
    public void E2_a_project_on_the_channel_takes_the_channels_major_for_an_older_companion()
    {
        var outcome = Run(Dotnet("11.0", false), null, Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0"));

        Moves(outcome, Hosting).Should().Equal("11.0.0");
        GroupOf(outcome, Hosting).Should().Be("platform:dotnet 11.0");
    }

    /// <summary>E3: a project that targets an older channel than the SDK's keeps its packages' major (it must keep building
    /// there): the patch is planned, the next major waits for approval.</summary>
    [Fact]
    public void E3_a_project_on_an_older_channel_keeps_its_major()
    {
        var outcome = Run(Dotnet("11.0", false, "net10.0"), null, Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0"));

        Moves(outcome, Hosting).Should().Equal("10.0.15");
        outcome.AwaitingApproval.Should().Contain(e => e.DepName == Hosting && e.Target == "11.0.0");
    }

    /// <summary>E4: a .NET Standard project has no platform to move: its references are ordinary packages.</summary>
    [Fact]
    public void E4_a_netstandard_project_has_no_platform_so_its_packages_are_ordinary()
    {
        var outcome = Run(Dotnet("11.0", false, "netstandard2.0"), null, Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0"));

        Moves(outcome, Hosting).Should().Equal("10.0.15");
        GroupOf(outcome, Hosting).Should().Be(Hosting);
    }

    // ═══ A preview SDK allows preview packages ═══════════════════════════════════════════════════════════════════════

    /// <summary>E5: on an SDK still on its RC, an older-major companion of a project on the channel takes the channel's
    /// prerelease (there is no release yet).</summary>
    [Fact]
    public void E5_on_a_preview_SDK_an_older_companion_takes_the_channels_prerelease()
    {
        var outcome = Run(Dotnet("11.0", true), null, Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0-rc.2.26504.105"));

        Moves(outcome, Hosting).Should().Equal("11.0.0-rc.2.26504.105");
    }

    /// <summary>E5b: a rule's <c>track: stable</c> keeps that companion on stables, even on a preview SDK.</summary>
    [Fact]
    public void E5b_on_a_preview_SDK_a_stable_track_rule_keeps_a_companion_on_stables()
    {
        var outcome = Run(Dotnet("11.0", true), $"dependencies:\n  rules:\n    - match: [\"{Hosting}\"]\n      track: stable\n",
            Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0-rc.2.26504.105"));

        Moves(outcome, Hosting).Should().Equal("10.0.15");
    }

    /// <summary>E5c: on a preview SDK, a project that still targets the previous channel is not pulled onto the preview.</summary>
    [Fact]
    public void E5c_on_a_preview_SDK_a_project_on_the_previous_channel_stays_on_stables()
    {
        var outcome = Run(Dotnet("11.0", true, "net10.0"), null, Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0-rc.2.26504.105"));

        Moves(outcome, Hosting).Should().Equal("10.0.15");
    }

    /// <summary>E6: on a preview SDK, an ordinary package on a stable is still offered stables only.</summary>
    [Fact]
    public void E6_on_a_preview_SDK_an_ordinary_stable_package_stays_on_stables()
    {
        var outcome = Run(Dotnet("11.0", true), null, Nuget(Json, "13.0.3", Web, "13.0.4", "13.1.0-beta.1"));

        Moves(outcome, Json).Should().Equal("13.0.4");
        outcome.Available.Should().Contain(e => e.DepName == Json && e.Reason == EDependencyReportReason.PreviewNotFollowed);
    }

    /// <summary>E7: <c>platforms.dotnet.track: preview</c> puts the SDK's companions on previews too, even on a stable.</summary>
    [Fact]
    public void E7_a_preview_track_SDK_offers_its_companions_previews()
    {
        var outcome = Run(Dotnet("11.0", false), "dependencies:\n  platforms:\n    dotnet: { track: preview }\n",
            Nuget(Di, "11.0.0", Web, "11.0.1", "11.1.0-preview.1"));

        Moves(outcome, Di).Should().Equal("11.1.0-preview.1");
    }

    // ═══ A stable SDK allows stable packages ════════════════════════════════════════════════════════════════════════

    /// <summary>E8: on a released SDK, a companion on a stable takes the stable patch, never the newer preview, and the
    /// preview is listed with the rule that would follow it.</summary>
    [Fact]
    public void E8_on_a_stable_SDK_a_stable_companion_is_offered_stables_only()
    {
        var outcome = Run(Dotnet("11.0", false), null, Nuget(Di, "11.0.0", Web, "11.0.1", "11.1.0-preview.1"));

        Moves(outcome, Di).Should().Equal("11.0.1");
        outcome.Available.Should().Contain(e => e.DepName == Di && e.Reason == EDependencyReportReason.PreviewNotFollowed);
    }

    /// <summary>E9: a preview of the next major (above the SDK's channel) is not offered to a stable companion.</summary>
    [Fact]
    public void E9_on_a_stable_SDK_a_next_major_preview_is_not_offered()
    {
        var outcome = Run(Dotnet("11.0", false), AllMajor, Nuget(Di, "11.0.0", Web, "12.0.0-preview.1"));

        Moves(outcome, Di).Should().BeEmpty();
    }

    /// <summary>E10 (exception, prerelease-dependencies-PRD H1): a package already on a prerelease of a major above the
    /// SDK's follows its prereleases as an ordinary package; it is no companion of the channel.</summary>
    [Fact]
    public void E10_a_package_on_a_next_major_prerelease_follows_its_prereleases_as_an_ordinary_package()
    {
        var outcome = Run(Dotnet("11.0", false), null, Nuget(Di, "12.0.0-preview.1", Web, "12.0.0-preview.2"));

        Moves(outcome, Di).Should().Equal("12.0.0-preview.2");
        GroupOf(outcome, Di).Should().NotStartWith("platform:");
    }

    // ═══ coverage.yml and account exceptions ═══════════════════════════════════════════════════════════════════════

    /// <summary>E11: an ignored companion is left alone, however far behind the channel it is.</summary>
    [Fact]
    public void E11_an_ignored_companion_does_not_move()
    {
        var outcome = Run(Dotnet("11.0", false), $"dependencies:\n  ignore: [\"{Hosting}\"]\n", Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0"));

        Moves(outcome, Hosting).Should().BeEmpty();
    }

    /// <summary>E12: <c>track: stable</c> on a companion on an rc takes the release like the default does.</summary>
    [Fact]
    public void E12_a_stable_track_rule_on_an_rc_takes_the_release()
    {
        var outcome = Run(Dotnet("11.0", false), $"dependencies:\n  rules:\n    - match: [\"{Di}\"]\n      track: stable\n",
            Nuget(Di, Rc1, Web, "11.0.0", "11.1.0-preview.1"));

        Moves(outcome, Di).Should().Equal("11.0.0");
    }

    /// <summary>E13: an enforced stable account rule wins over the file's <c>track: preview</c>: the rc takes the release.</summary>
    [Fact]
    public void E13_an_enforced_stable_account_rule_wins_over_a_preview_rule_on_a_companion()
    {
        var enforced = AccountRule("Microsoft.Extensions.*", EDependencyThreshold.Major);
        enforced.Track = EDependencyTrack.Stable;
        var config = Config($"dependencies:\n  rules:\n    - match: [\"{Di}\"]\n      track: preview\n", enforced);

        var outcome = RunWith(Dotnet("11.0", false), config, Nuget(Di, Rc1, Web, "11.0.0", "11.1.0-preview.1"));

        Moves(outcome, Di).Should().Equal("11.0.0");
    }

    /// <summary>E14: a non-enforced account <c>track: preview</c> counts as an explicit choice, like the file's.</summary>
    [Fact]
    public void E14_an_account_preview_rule_keeps_a_companion_on_prereleases()
    {
        var advisory = AccountRule("Microsoft.Extensions.*", EDependencyThreshold.Major, enforced: false);
        advisory.Track = EDependencyTrack.Preview;

        var outcome = RunWith(Dotnet("11.0", false), Config(null, advisory), Nuget(Di, Rc1, Web, "11.0.0", "11.1.0-preview.1"));

        Moves(outcome, Di).Should().Equal("11.1.0-preview.1");
    }

    /// <summary>E15: a held companion stays where it is, and the hold is reported.</summary>
    [Fact]
    public void E15_a_held_companion_does_not_move()
    {
        var outcome = Run(Dotnet("11.0", false), $"dependencies:\n  rules:\n    - match: [\"{Hosting}\"]\n      hold: true\n",
            Nuget(Hosting, "10.0.5", Web, "10.0.15", "11.0.0"));

        Moves(outcome, Hosting).Should().BeEmpty();
    }
}
