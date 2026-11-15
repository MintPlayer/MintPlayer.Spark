using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using CodeCoverage.Entities;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// prerelease-dependencies-PRD §5.1 (A1-A8): a package's track. MintPlayer.Dotnet.Tools' MintPlayer.GraphQL depends on
/// Octokit.GraphQL, which has only ever shipped betas (<c>0.1.0-beta</c> … <c>0.4.0-beta</c>). H1: a package whose current
/// version is a prerelease follows newer prereleases on the default track; <c>track: stable</c> opts out. D2: the engine
/// screens every package candidate against the track, so an enforced account rule narrows it. D3: a prerelease the engine
/// drops leaves a line naming the setting that follows it.
/// </summary>
public class PackagePrereleaseTrackTests
{
    /// <summary>A NuGet reference in a csproj, the shape Renovate reports for Tools' GraphQL project.</summary>
    private static LookupRecord NuGet(string depName, string current, params LookupCandidate[] candidates) => Record(depName, current, candidates) with
    {
        Ecosystem = "nuget",
        Manager = "nuget",
        Datasource = "nuget",
        PackageFile = "GraphQL/MintPlayer.GraphQL.Tools/MintPlayer.GraphQL.Tools.csproj",
        LockFiles = [],
        DepType = "nuget",
    };

    private static DependencyRule Tracked(DependencyRule rule, EDependencyTrack track)
    {
        rule.Track = track;
        return rule;
    }

    private const string AllMinor = "dependencies:\n  autoMerge: { default: minor }\n";

    /// <summary>R17: in a 0.x package a minor is breaking, so 0.3.0-beta → 0.4.0-beta is a major; these tests admit it.</summary>
    private const string AllMajor = "dependencies:\n  autoMerge: { default: major }\n";

    [Fact]
    public void A1_a_package_on_a_beta_follows_the_next_beta_on_the_default_track()
    {
        var octokit = NuGet("Octokit.GraphQL", "0.3.0-beta", Candidate(EUpdateType.Minor, "0.4.0-beta"));

        var outcome = Evaluate(Lookup(octokit), Config(AllMajor));

        Planned(outcome).Should().Equal("Octokit.GraphQL: Octokit.GraphQL@0.4.0-beta");
    }

    [Fact]
    public void A2_the_next_prerelease_of_the_same_core_is_a_patch()
    {
        var pkg = NuGet("Acme.Lib", "1.0.0-beta.1", Candidate(EUpdateType.Patch, "1.0.0-beta.2"));

        var outcome = Evaluate(Lookup(pkg));

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.Level.Should().Be(EUpdateLevel.Patch);
        group.AutoMerge.Should().BeTrue();
    }

    [Fact]
    public void A3_the_first_stable_after_a_zero_major_beta_waits_like_any_major()
    {
        var octokit = NuGet("Octokit.GraphQL", "0.4.0-beta", Candidate(EUpdateType.Major, "1.0.0"));

        var outcome = Evaluate(Lookup(octokit));

        outcome.Groups.Should().BeEmpty();
        outcome.AwaitingApproval.Should().ContainSingle(e => e.DepName == "Octokit.GraphQL" && e.Target == "1.0.0");
    }

    [Fact]
    public void A4_a_preview_rule_lets_a_stable_package_take_a_prerelease()
    {
        var pkg = NuGet("Acme.Lib", "1.2.0", Candidate(EUpdateType.Minor, "1.3.0-beta.1"));

        var outcome = Evaluate(Lookup(pkg), Config(AllMinor + "  rules:\n    - match: \"Acme.Lib\"\n      track: preview\n"));

        Planned(outcome).Should().Equal("Acme.Lib: Acme.Lib@1.3.0-beta.1");
    }

    [Fact]
    public void A5_an_enforced_stable_account_rule_wins_over_the_files_preview_rule()
    {
        var pkg = NuGet("Acme.Lib", "1.2.0", Candidate(EUpdateType.Minor, "1.3.0-beta.1"));
        var enforced = Tracked(AccountRule("Acme.*", EDependencyThreshold.Major), EDependencyTrack.Stable);

        var outcome = Evaluate(Lookup(pkg), Config(AllMinor + "  rules:\n    - match: \"Acme.Lib\"\n      track: preview\n", enforced));

        outcome.Groups.Should().BeEmpty();
        var line = outcome.Available.Should().ContainSingle(e => e.Reason == EDependencyReportReason.PreviewNotFollowed).Which;
        line.Target.Should().Be("1.3.0-beta.1");
        line.Detail.Should().Contain("an enforced account rule");
    }

    [Fact]
    public void A6_a_stable_package_takes_the_stable_release_beside_a_prerelease_and_says_so()
    {
        var pkg = NuGet("Acme.Lib", "1.2.0", Candidate(EUpdateType.Minor, "1.4.0-beta.1"), Candidate(EUpdateType.Minor, "1.3.0"));

        var outcome = Evaluate(Lookup(pkg), Config(AllMinor));

        Planned(outcome).Should().Equal("Acme.Lib: Acme.Lib@1.3.0");
        var line = outcome.Available.Should().ContainSingle(e => e.Reason == EDependencyReportReason.PreviewNotFollowed).Which;
        line.Target.Should().Be("1.4.0-beta.1");
        line.Detail.Should().Contain("track: preview");
    }

    [Fact]
    public void A7_track_stable_opts_a_beta_package_out_of_prereleases_and_names_the_rule()
    {
        var octokit = NuGet("Octokit.GraphQL", "0.3.0-beta", Candidate(EUpdateType.Minor, "0.4.0-beta"));

        var outcome = Evaluate(Lookup(octokit), Config(AllMajor + "  rules:\n    - match: \"Octokit.GraphQL\"\n      track: stable\n"));

        outcome.Groups.Should().BeEmpty();
        var line = outcome.Available.Should().ContainSingle(e => e.Reason == EDependencyReportReason.PreviewNotFollowed).Which;
        line.Detail.Should().Contain("track: stable");
        var body = DependencyPullRequestBody.Render(new DependencyUpdateRun(), new DependencyRunReport { Available = [line] }, null);
        body.Should().Contain("`Octokit.GraphQL` 0.3.0-beta → 0.4.0-beta");
    }

    [Fact]
    public void A8_two_projects_on_the_same_beta_are_one_group_with_both_files()
    {
        var a = NuGet("Octokit.GraphQL", "0.3.0-beta", Candidate(EUpdateType.Minor, "0.4.0-beta"));
        var b = a with { PackageFile = "GraphQL/Other/Other.csproj" };

        var outcome = Evaluate(Lookup(a, b), Config(AllMajor));

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.Items.Select(i => i.Record.PackageFile).Should().BeEquivalentTo([a.PackageFile, b.PackageFile]);
    }

    /// <summary>H5: a hyphen means a prerelease only in the SemVer ecosystems. A Docker tag (<c>20-alpine</c>) is no preview.</summary>
    [Fact]
    public void A_hyphen_outside_the_semver_ecosystems_is_no_prerelease()
    {
        var image = Record("node", "20-alpine", Candidate(EUpdateType.Major, "22-alpine")) with { Ecosystem = "docker", Manager = "dockerfile", Datasource = "docker", PackageFile = "Dockerfile", LockFiles = [] };

        var outcome = Evaluate(Lookup(image), Config("dependencies:\n  autoMerge: { default: major }\n"));

        Planned(outcome).Should().Equal("node: node@22-alpine");
    }

    [Theory]
    [InlineData("nuget", "0.3.0-beta", EDependencyTrack.Preview)]
    [InlineData("npm", "1.0.0-rc.1", EDependencyTrack.Preview)]
    [InlineData("nuget", "[0.3.0-beta]", EDependencyTrack.Preview)]
    [InlineData("nuget", "1.2.0", EDependencyTrack.Stable)]
    [InlineData("docker", "20-alpine", EDependencyTrack.Stable)]
    [InlineData("maven", "33.0.0-jre", EDependencyTrack.Stable)]
    [InlineData("npm", "1.0.0 - 2.0.0", EDependencyTrack.Stable)]
    public void The_default_track_follows_the_current_version(string ecosystem, string current, EDependencyTrack expected)
        => Config().PackageTrack(ecosystem, "Some.Package", current).Should().Be(expected);

    /// <summary>#606 D8 (A9's sibling): a companion of an SDK on the preview track follows previews without a rule of its own.</summary>
    [Fact]
    public void A_companion_of_a_preview_sdk_is_on_the_preview_track()
        => Config("dependencies:\n  platforms:\n    node: { track: preview }\n").PackageTrack("npm", "@types/node", "22.18.0").Should().Be(EDependencyTrack.Preview);

    /// <summary>H5: what Renovate is told to look up stable-only, in order after the prerelease-current rule.</summary>
    [Fact]
    public void The_stable_globs_are_the_files_stable_rules_and_every_enforced_stable_rule()
    {
        var enforced = Tracked(AccountRule("Acme.*", EDependencyThreshold.Major), EDependencyTrack.Stable);
        var advisory = Tracked(AccountRule("Other.*", EDependencyThreshold.Major, enforced: false), EDependencyTrack.Stable);
        var config = Config("dependencies:\n  rules:\n    - match: \"Octokit.GraphQL\"\n      track: stable\n    - match: \"node\"\n      ecosystem: \"platform:node\"\n      track: stable\n", enforced, advisory);

        config.StableGlobs().Should().Equal("Octokit.GraphQL", "Acme.*");
    }
}
