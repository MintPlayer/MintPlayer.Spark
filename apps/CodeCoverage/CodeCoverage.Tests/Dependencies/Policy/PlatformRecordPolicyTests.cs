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
        // #606 D8: the preview track offers it; being on a preview no longer does by itself.
        var config = Config("dependencies:\n  platforms:\n    dotnet: { track: preview, lockPackageMajor: true }\n");
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
        platform.Lagging.Should().BeNull("platform-locations-PRD P6: auto is the action's default, so an older action never sees the key");
        DependencyContractReader.WritePlan(outcome.ToPlan("platform-1", Sha));

        var reporting = Config("dependencies:\n  platforms:\n    dotnet: { track: preview, lockPackageMajor: true, lagging: report }\n");
        var approved = Evaluate(lookup, reporting, waiting.NextState, now: Later, configBlob: "b");
        approved.Groups.Should().ContainSingle().Which.Platform!.Lagging.Should().Be("report");
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
        // platform-locations-PRD P10: a node move carries the runtimes GitHub runs, for the actions' runs.using.
        group.Platform!.ActionRuntimes.Should().Equal(["node20", "node24"]);
        DependencyContractReader.WritePlan(outcome.ToPlan("platform-node", Sha));
    }

    [Fact]
    public void A_platform_found_only_under_dependencies_exclude_is_not_moved()
    {
        // platform-locations-PRD S2: Spark's test fixtures held the only rust/go/php/python locations, and the bot planned
        // and applied platform moves on them. dependencies.exclude leaves their package files alone; their platform too.
        var node = Fixture("platform.s19-node.json") with
        {
            Current = new PlatformCurrent { Channels = ["20"] },
            Locations = [new PlatformLocation { File = "tests/Fixtures/app/.nvmrc", Kind = "version-file", Value = "20", Channel = "20" }],
        };
        var config = Config("dependencies:\n  exclude: [\"**/Fixtures/**\"]\n  autoMerge: { \"platform:node\": major }\n");

        var outcome = Evaluate(WithPlatforms(node), config, now: Later);

        outcome.Groups.Should().BeEmpty();
        outcome.AwaitingApproval.Should().BeEmpty();
    }

    [Fact]
    public void Dependencies_exclude_reaches_the_platform_move_and_its_current_channel()
    {
        // The fixture on 20 neither sets the repository's channel nor gets moved: the plan carries the exclude.
        var node = Fixture("platform.s19-node.json") with
        {
            Current = new PlatformCurrent { Channels = ["22", "20"] },
            Locations = [
                new PlatformLocation { File = ".nvmrc", Kind = "version-file", Value = "22", Channel = "22" },
                new PlatformLocation { File = "tests/Fixtures/app/.nvmrc", Kind = "version-file", Value = "20", Channel = "20" },
            ],
        };
        var config = Config("dependencies:\n  exclude: [\"**/Fixtures/**\"]\n  autoMerge: { \"platform:node\": major }\n  platforms:\n    node: { exclude: [\"docs/**\"] }\n");

        var outcome = Evaluate(WithPlatforms(node), config, now: Later);

        var platform = outcome.Groups.Should().ContainSingle().Which.Platform!;
        platform.FromChannel.Should().Be("22");
        platform.Exclude.Should().Equal(["docs/**", "**/Fixtures/**"]);
    }

    [Fact]
    public void Only_a_node_move_carries_the_action_runtimes()
    {
        var dotnet = Fixture("platform.s19-dotnet.json") with { Current = new PlatformCurrent { Channels = ["10.0"], Preview = true } };
        var config = Config("dependencies:\n  platforms:\n    dotnet: { track: preview }\n");
        var waiting = Evaluate(WithPlatforms(dotnet), config, now: Later, configBlob: "b");
        DependencyHolds.Approve(waiting.NextState, waiting, "platform:dotnet 11.0", "b", "alice", 1, Later);

        var outcome = Evaluate(WithPlatforms(dotnet), config, waiting.NextState, now: Later, configBlob: "b");

        outcome.Groups.Should().ContainSingle().Which.Platform!.ActionRuntimes.Should().BeNull();
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

    /// <summary>
    /// platform-locations-PRD P11: a location on the moved channel that the bot cannot edit would stay behind, and an
    /// in-channel move (a patch) otherwise merges through the queue unseen. It waits for 'approve'; exclude lifts it.
    /// </summary>
    [Fact]
    public void A_same_channel_location_the_bot_cannot_edit_holds_the_move_for_approval()
    {
        var stuck = new PlatformLocation { File = "eng/Versions.props", Kind = "tfm", Value = "$(Tfm)", Channel = "10.0", Computed = true, Reason = "a property" };
        var editable = new PlatformLocation { File = "global.json", Kind = "global.json", Value = "10.0.100", Channel = "10.0", Computed = false };
        var patch = new PlatformCandidate { Channel = "10.0", LatestVersion = "10.0.5", LatestSdk = "10.0.105", ReleaseTimestamp = "2026-10-01" };
        var record = new PlatformRecord { Sdk = "dotnet", Source = "releases-index", Current = new PlatformCurrent { Channels = ["10.0"] }, Locations = [stuck, editable], Candidates = [patch] };
        var lookup = WithPlatforms(record);

        var waiting = Evaluate(lookup, now: Later, configBlob: "b");
        waiting.Groups.Should().BeEmpty();
        waiting.AwaitingApproval.Should().ContainSingle().Which.Detail.Should().Contain("eng/Versions.props").And.Contain("platforms.dotnet.exclude");
        DependencyHolds.Approve(waiting.NextState, waiting, "platform:dotnet 10.0", "b", "alice", 1, Later).Should().NotBeNull();
        Evaluate(lookup, state: waiting.NextState, now: Later, configBlob: "b").Groups.Should().ContainSingle().Which.AutoMerge.Should().BeFalse("an approved move never auto-merges");

        var excluded = Evaluate(lookup, Config("dependencies:\n  platforms:\n    dotnet: { exclude: [\"eng/**\"] }\n"), now: Later);
        excluded.Groups.Should().ContainSingle("an excluded location stays behind on purpose");

        var olderChannel = Evaluate(WithPlatforms(record with { Locations = [stuck with { Channel = "9.0" }, editable] }), now: Later);
        olderChannel.Groups.Should().ContainSingle("an older channel is information only");
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
