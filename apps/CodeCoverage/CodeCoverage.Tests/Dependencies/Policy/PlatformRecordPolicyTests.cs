using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>Platform/SDK records (PRD R7, R8, D-S12c) on the REAL S19 and S12 platform fixtures: a platform major never
/// auto-merges, previews are followed only from a preview, and a computed location is reported, never planned.</summary>
public class PlatformRecordPolicyTests
{
    private static PlatformRecord Fixture(string name) => DependencyContractReader.ReadPlatform(ContractFixture("valid", name)).Document!;

    private static LookupDocument WithPlatforms(params PlatformRecord[] platforms) => Lookup() with { Platforms = [.. platforms] };

    private static readonly DateTimeOffset Later = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_preview_channel_is_not_followed_from_a_stable_one()
    {
        // S19: this repository on net10.0, the only newer channel 11.0 still a preview.
        var dotnet = Fixture("platform.s19-dotnet.json");
        dotnet.Current.Preview.Should().BeFalse();

        var outcome = Evaluate(WithPlatforms(dotnet), now: Later);

        outcome.Groups.Should().BeEmpty();
        outcome.Available.Should().ContainSingle().Which.Reason.Should().Be(EDependencyReportReason.PreviewNotFollowed);
    }

    [Fact]
    public void From_a_preview_the_next_preview_major_waits_for_approval_and_is_planned_for_the_SDK_adapter_once_approved()
    {
        var dotnet = Fixture("platform.s19-dotnet.json") with { Current = new PlatformCurrent { Channels = ["10.0"], Preview = true } };
        var config = Config("dependencies:\n  platforms:\n    dotnet: { lockPackageMajor: true }\n");
        var lookup = WithPlatforms(dotnet);

        var waiting = Evaluate(lookup, config, now: Later, configBlob: "b");
        waiting.Groups.Should().BeEmpty();
        waiting.AwaitingApproval.Should().ContainSingle().Which.Group.Should().Be("platform:dotnet 11.0");
        DependencyHolds.Approve(waiting.NextState, waiting, "platform:dotnet 11.0", "b", "alice", 1, Later).Should().NotBeNull();

        var outcome = Evaluate(lookup, config, waiting.NextState, now: Later, configBlob: "b");

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.AutoMerge.Should().BeFalse("R7: a platform major never auto-merges");
        group.Items.Should().BeEmpty();
        var platform = group.Platform!;
        platform.Sdk.Should().Be("dotnet");
        platform.FromChannel.Should().Be("10.0");
        platform.ToChannel.Should().Be("11.0");
        platform.ToVersion.Should().Be("11.0.0-rc.2");
        platform.SdkVersion.Should().Be("11.0.100-rc.2.26504.105");
        platform.Strategy.Should().Be("replace");
        platform.LockPackageMajor.Should().Be(true);
        DependencyContractReader.WritePlan(outcome.ToPlan("platform-1", Sha));
    }

    [Fact]
    public void A_platform_major_the_file_admits_is_planned_but_still_never_auto_merges()
    {
        // S19: node 22 -> 26 (LTS, 2026-10-14).
        var node = Fixture("platform.s19-node.json");
        var config = Config("dependencies:\n  autoMerge: { \"platform:node\": major }\n");

        var outcome = Evaluate(WithPlatforms(node), config, now: Later);

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.Group.Should().Be("platform:node 26");
        group.Level.Should().Be(EUpdateLevel.Major);
        group.AutoMerge.Should().BeFalse();
    }

    [Fact]
    public void A_platform_minor_line_change_may_auto_merge()
    {
        // S12: python 3.11 -> 3.15 keeps the leading segment, so it is a minor.
        var lookup = DependencyContractReader.ReadLookup(ContractFixture("valid", "lookup.docker-actions-platform.json")).Document!;
        var python = lookup.Platforms.Single(p => p.Sdk == "python");

        var outcome = Evaluate(WithPlatforms(python), now: Later);

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.Group.Should().Be("platform:python 3.15");
        group.Level.Should().Be(EUpdateLevel.Minor);
        group.AutoMerge.Should().BeTrue();
    }

    [Fact]
    public void A_computed_location_is_reported_never_planned()
    {
        var computed = new PlatformLocation { File = "src/Lib/Lib.csproj", Kind = "tfm", Value = "$(LibTargetFramework)", Channel = null, Computed = true, Reason = "a property" };
        var editable = new PlatformLocation { File = "global.json", Kind = "global.json", Value = "10.0.100", Channel = "10.0", Computed = false };
        var candidate = new PlatformCandidate { Channel = "10.1", LatestVersion = "10.1.0", ReleaseTimestamp = "2026-10-01" };
        var mixed = new PlatformRecord { Sdk = "dotnet", Source = "releases-index", Current = new PlatformCurrent { Channels = ["10.0"] }, Locations = [computed, editable], Candidates = [candidate] };

        var outcome = Evaluate(WithPlatforms(mixed), now: Later);
        outcome.PlatformReports.Should().ContainSingle().Which.File.Should().Be("src/Lib/Lib.csproj");
        outcome.Groups.Should().ContainSingle("the editable location is still planned");

        var onlyComputed = Evaluate(WithPlatforms(mixed with { Locations = [computed] }), now: Later);
        onlyComputed.PlatformReports.Should().ContainSingle();
        onlyComputed.Groups.Should().BeEmpty("R8: a computed TFM is reported, never guessed");
    }

    [Fact]
    public void A_platform_channel_younger_than_the_cooldown_waits()
    {
        var node = Fixture("platform.s19-node.json");
        var config = Config("dependencies:\n  autoMerge: { \"platform:node\": major }\n");

        var outcome = Evaluate(WithPlatforms(node), config, now: new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));

        outcome.Groups.Should().BeEmpty();
        outcome.Available.Should().ContainSingle().Which.Reason.Should().Be(EDependencyReportReason.Cooldown);
    }
}
