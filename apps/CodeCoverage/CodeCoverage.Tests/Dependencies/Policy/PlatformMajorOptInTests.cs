using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// prerelease-dependencies-PRD H3 (C5, C6, C10): D-P18/R7 keep a platform major (.NET 11 → 12) for a person. The owner
/// asked why 10 → 11 needs an approval, and chose an explicit per-repository opt-in, <c>platforms.&lt;sdk&gt;.autoMerge: major</c>.
/// Without it nothing changes; an enforced account rule still wins over it.
/// </summary>
public class PlatformMajorOptInTests
{
    private static readonly DateTimeOffset Later = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A repository on .NET 11 GA with 12.0 on offer (a major), and nothing inside 11.0 to take instead.</summary>
    private static LookupDocument OnEleven() => Lookup() with
    {
        Platforms =
        [
            new PlatformRecord
            {
                Sdk = "dotnet",
                Source = "releases-index",
                Current = new PlatformCurrent { Channels = ["11.0"] },
                Locations =
                [
                    new PlatformLocation { File = "global.json", Kind = "global.json", Value = "11.0.100", Channel = "11.0" },
                    new PlatformLocation { File = "src/Lib/Lib.csproj", Kind = "tfm", Value = "net11.0", Channel = "11.0" },
                ],
                Candidates = [new PlatformCandidate { Channel = "12.0", LatestVersion = "12.0.0", LatestSdk = "12.0.100", ReleaseTimestamp = "2026-11-10", Lts = true }],
            },
        ],
    };

    private const string OptIn = "dependencies:\n  platforms:\n    dotnet: { autoMerge: major }\n";

    [Fact]
    public void C5_by_default_a_platform_major_waits_for_approval()
    {
        var outcome = Evaluate(OnEleven(), now: Later);

        outcome.Groups.Should().BeEmpty();
        outcome.AwaitingApproval.Should().ContainSingle().Which.Detail.Should().StartWith("platform major 11.0 → 12.0");
    }

    [Fact]
    public void C6_admitting_majors_plans_the_move_but_never_auto_merges_it()
    {
        var outcome = Evaluate(OnEleven(), Config("dependencies:\n  autoMerge: { \"platform:dotnet\": major }\n"), now: Later);

        var group = outcome.Groups.Should().ContainSingle().Which;
        (group.Group, group.Level, group.AutoMerge).Should().Be(("platform:dotnet 12.0", EUpdateLevel.Major, false));
    }

    [Fact]
    public void C10_the_opt_in_auto_merges_the_platform_major()
    {
        var outcome = Evaluate(OnEleven(), Config(OptIn), now: Later);

        var group = outcome.Groups.Should().ContainSingle().Which;
        (group.Group, group.Level, group.AutoMerge, group.Approved).Should().Be(("platform:dotnet 12.0", EUpdateLevel.Major, true, false));
    }

    // D8: the rule's match may name the SDK or the platform ecosystem; both cap the move.
    [Theory]
    [InlineData("dotnet")]
    [InlineData("platform:dotnet")]
    [InlineData("dot*")]
    public void C10_an_enforced_account_rule_still_wins_over_the_opt_in(string match)
    {
        var rule = AccountRule(match, EDependencyThreshold.Minor);
        rule.Ecosystem = "platform:dotnet";

        var outcome = Evaluate(OnEleven(), Config(OptIn, rule), now: Later);

        outcome.Groups.Should().BeEmpty();
        outcome.Held.Should().ContainSingle(e => e.Reason == EDependencyReportReason.AboveAccountRule);
    }

    [Fact]
    public void The_opt_in_of_one_sdk_leaves_the_others_alone()
        => Evaluate(OnEleven(), Config("dependencies:\n  platforms:\n    node: { autoMerge: major }\n"), now: Later).Groups.Should().BeEmpty();

    [Theory]
    [InlineData("minor")]
    [InlineData("always")]
    public void Major_is_the_only_value(string value)
        => DependenciesYml.Parse($"dependencies:\n  platforms:\n    dotnet: {{ autoMerge: {value} }}\n").Errors.Should()
            .ContainSingle().Which.Should().Contain("dependencies.platforms.dotnet.autoMerge");
}
