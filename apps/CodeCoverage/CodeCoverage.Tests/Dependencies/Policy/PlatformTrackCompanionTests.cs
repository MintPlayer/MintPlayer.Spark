using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using CodeCoverage.Entities;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// #606 D2, D3 and D8 on the REAL S19 node platform record (node 22, offered 26 and 24, both LTS): the track picks the
/// channel, a companion (<c>@types/node</c>) rides in the SDK's group at the new major, and the move is pending while a
/// companion has no release there, until a writer approves it with <c>--force</c>.
/// </summary>
public class PlatformTrackCompanionTests
{
    private static readonly DateTimeOffset Later = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>R7 holds a platform major for approval; this file admits it, so each test sees the companion rules alone.</summary>
    private const string AdmitMajor = "dependencies:\n  autoMerge: { \"platform:node\": major }\n";

    private static PlatformRecord Node() => DependencyContractReader.ReadPlatform(ContractFixture("valid", "platform.s19-node.json")).Document!;

    private static PlatformCandidate Channel(string channel, bool preview = false, bool? lts = null)
        => new() { Channel = channel, LatestVersion = $"{channel}.0.0", ReleaseTimestamp = "2026-09-01", Preview = preview, Lts = lts };

    /// <summary>Node with a preview 27, a non-LTS 25 and the fixture's LTS 24 on offer.</summary>
    private static PlatformRecord NodeOnEveryTrack() => Node() with { Candidates = [Channel("27", preview: true), Channel("25", lts: false), Channel("24", lts: true)] };

    private static LookupDocument With(PlatformRecord platform, params LookupRecord[] records) => Lookup(records) with { Platforms = [platform] };

    private static LookupRecord TypesNode(params LookupCandidate[] candidates) => Record("@types/node", "22.18.0", candidates);

    private static DependencyRule Tracked(DependencyRule rule, EDependencyTrack track, string? ecosystem = null)
    {
        rule.Track = track;
        rule.Ecosystem = ecosystem;
        return rule;
    }

    [Theory]
    [InlineData(null, "platform:node 25")]
    [InlineData("stable", "platform:node 25")]
    [InlineData("lts", "platform:node 24")]
    [InlineData("preview", "platform:node 27")]
    public void The_track_picks_the_channel(string? track, string group)
    {
        var yml = AdmitMajor + (track is null ? "" : $"  platforms:\n    node: {{ track: {track} }}\n");

        var outcome = Evaluate(With(NodeOnEveryTrack()), Config(yml), now: Later);

        outcome.Groups.Should().ContainSingle().Which.Group.Should().Be(group);
    }

    [Fact]
    public void A_channel_off_the_track_is_reported_with_the_setting_that_follows_it()
    {
        var outcome = Evaluate(With(NodeOnEveryTrack()), Config(AdmitMajor), now: Later);

        var entry = outcome.Available.Should().ContainSingle(e => e.Reason == EDependencyReportReason.PreviewNotFollowed).Which;
        entry.Target.Should().Be("27");
        entry.Detail.Should().Contain("platforms.node.track: preview");
    }

    [Fact]
    public void An_enforced_account_rule_narrows_the_files_track()
    {
        var rule = Tracked(AccountRule("node", EDependencyThreshold.Major), EDependencyTrack.Lts, "platform:node");
        var config = Config(AdmitMajor + "  platforms:\n    node: { track: preview }\n", rule);

        config.PlatformTrack("node").Should().Be(EDependencyTrack.Lts);
    }

    [Fact]
    public void Lts_on_an_sdk_without_lts_releases_reads_as_stable()
    {
        var rule = Tracked(AccountRule("go", EDependencyThreshold.Major, enforced: false), EDependencyTrack.Lts, "platform:go");

        Config(null, rule).PlatformTrack("go").Should().Be(EDependencyTrack.Stable);
    }

    [Fact]
    public void A_companion_rides_in_its_sdks_group_at_the_new_major()
    {
        var types = TypesNode(Candidate(EUpdateType.Major, "26.0.1"), Candidate(EUpdateType.Minor, "22.19.0"));

        var outcome = Evaluate(With(Node(), types), Config(AdmitMajor), now: Later);

        Planned(outcome).Should().Equal("platform:node 26: @types/node@26.0.1");
    }

    [Fact]
    public void A_companion_keeps_its_own_minor_when_its_sdk_does_not_move()
    {
        // No platform group (R7 holds node 26 for approval), so @types/node's 26.x is listed as moving with node, and its minor
        // is planned on its own.
        var types = TypesNode(Candidate(EUpdateType.Major, "26.0.1"), Candidate(EUpdateType.Minor, "22.19.0"));

        var outcome = Evaluate(With(Node(), types), now: Later);

        Planned(outcome).Should().Equal("@types/node: @types/node@22.19.0");
        outcome.Available.Should().Contain(e => e.DepName == "@types/node" && e.Reason == EDependencyReportReason.MovesWithPlatform && e.Target == "26.0.1");
    }

    [Fact]
    public void A_record_on_another_major_than_its_sdk_is_not_a_companion()
    {
        var types = Record("@types/node", "20.19.0", Candidate(EUpdateType.Major, "26.0.1"));

        var outcome = Evaluate(With(Node(), types), Config(AdmitMajor + "  rules:\n    - match: \"@types/node\"\n      allowMajor: true\n"), now: Later);

        outcome.Groups.Single(g => g.Group == "platform:node 26").Items.Should().BeEmpty();
        Planned(outcome).Should().Contain("@types/node: @types/node@26.0.1");
    }

    [Fact]
    public void A_platform_kept_beside_the_previous_one_has_no_companions()
    {
        var types = TypesNode(Candidate(EUpdateType.Major, "26.0.1"));

        var outcome = Evaluate(With(Node(), types), Config(AdmitMajor + "  platforms:\n    node: { strategy: add }\n"), now: Later);

        outcome.Groups.Single(g => g.Group == "platform:node 26").Items.Should().BeEmpty();
        outcome.Available.Should().NotContain(e => e.Reason == EDependencyReportReason.MovesWithPlatform);
    }

    [Fact]
    public void A_major_move_is_pending_while_a_companion_has_no_release_there_until_forced()
    {
        var types = TypesNode(Candidate(EUpdateType.Minor, "22.19.0"));
        var lookup = With(Node(), types);
        var config = Config(AdmitMajor);

        var pending = Evaluate(lookup, config, now: Later, configBlob: "b");

        pending.Groups.Should().NotContain(g => g.Group.StartsWith("platform:", StringComparison.Ordinal));
        var held = pending.Held.Should().ContainSingle(e => e.Reason == EDependencyReportReason.Pending).Which;
        held.Group.Should().Be("platform:node 26");
        held.Detail.Should().Be("waiting on @types/node to release 26.x: comment 'approve platform:node 26 --force' to move without them");
        DependencyHolds.Approve(pending.NextState, pending, "platform:node 26", "b", "alice", 1, Later).Should().BeNull("only --force approves a pending move");

        DependencyHolds.Approve(pending.NextState, pending, "platform:node 26", "b", "alice", 1, Later, force: true)!.Force.Should().BeTrue();
        var forced = Evaluate(lookup, config, pending.NextState, now: Later, configBlob: "b");

        forced.Groups.Should().Contain(g => g.Group == "platform:node 26" && g.Items.Count == 0);
    }

    [Fact]
    public void The_unstable_globs_are_the_preview_rules_and_the_companions_of_a_preview_sdk()
    {
        var account = Tracked(AccountRule("@nx/*", EDependencyThreshold.Major, enforced: false), EDependencyTrack.Preview);
        var config = Config("dependencies:\n  rules:\n    - match: \"vitest\"\n      track: preview\n    - match: \"node\"\n      ecosystem: \"platform:node\"\n      track: preview\n"
            + "  platforms:\n    node: { track: preview }\n", account);

        config.UnstableGlobs().Should().Equal("vitest", "@nx/*", "@types/node");
    }
}
