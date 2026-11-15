using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// docs/code-coverage/dependency-companion-pins-PRD.md M3 (E, H2): a lockstep group moves only to a version every member has.
/// S6's registries: MCR publishes every Playwright release as <c>v1.65.1-noble</c>, NuGet's <c>Microsoft.Playwright</c> lags
/// by weeks and skips patches, and <c>ConnectAsync</c> refuses a client and server on different minors (#669's red). A
/// member without an update counts at its current version. Lockstep is implied for a group a <c>follow:</c> names, and
/// opt-in with <c>rules[].lockstep</c>.
/// </summary>
public class LockstepTests
{
    private const string Image = "mcr.microsoft.com/playwright";
    private const string Package = "Microsoft.Playwright";

    private const string Grouped = """
        dependencies:
          autoMerge: { default: major }
          rules:
            - match: ["Microsoft.Playwright", "mcr.microsoft.com/playwright"]
              group: playwright
        """;

    private const string Followed = Grouped + """

          follow:
            - dependency: mcr.microsoft.com/playwright
              path: apps/SeoTester/docker-compose.yml
              match: 'seotester-chromium:(?<version>[0-9.]+)'
        """;

    private const string OptedIn = """
        dependencies:
          autoMerge: { default: major }
          rules:
            - match: ["Microsoft.Playwright", "mcr.microsoft.com/playwright"]
              group: playwright
              lockstep: true
        """;

    private static LookupRecord Docker(string current, params string[] versions)
        => Record(Image, $"v{current}-noble", [.. versions.Select(v => Candidate(EUpdateType.Minor, v, value: $"v{v}-noble"))]) with
        {
            Ecosystem = "docker", Manager = "dockerfile", Datasource = "docker", PackageFile = "apps/SeoTester/chromium/Dockerfile", LockFiles = [],
            DepType = null, CurrentVersion = null,
        };

    private static LookupRecord NuGet(string current, params string[] versions)
        => Record(Package, current, [.. versions.Select(v => Candidate(EUpdateType.Minor, v))]) with
        {
            Ecosystem = "nuget", Manager = "nuget", Datasource = "nuget", PackageFile = "apps/SeoTester/SeoTester/SeoTester.csproj", LockFiles = [],
        };

    private static IReadOnlyList<(string, string)> Moves(DependencyPolicyOutcome outcome)
        => [.. outcome.Groups.SelectMany(g => g.Items).Select(i => (i.Record.DepName, i.ToPlanItem().NewValue))];

    [Fact]
    public void Without_lockstep_the_image_moves_past_the_package_which_is_669s_half_move()
    {
        var outcome = Evaluate(Lookup(Docker("1.64.1", "1.65.1"), NuGet("1.64.1")), Config(Grouped));

        Moves(outcome).Should().Equal((Image, "v1.65.1-noble"));
    }

    [Fact]
    public void A_group_a_follow_names_waits_until_every_member_has_the_version()
    {
        var outcome = Evaluate(Lookup(Docker("1.64.1", "1.65.1"), NuGet("1.64.1")), Config(Followed));

        outcome.Groups.Should().BeEmpty();
        outcome.Available.Should().Contain(e => e.Group == "playwright" && e.Reason == EDependencyReportReason.Lockstep
                                                && e.Detail == "lockstep: Microsoft.Playwright has no 1.65.1 yet");
    }

    [Fact]
    public void The_group_moves_to_the_highest_version_every_member_has()
    {
        // The image is offered 1.65.1 and 1.64.1, the package only 1.64.1: both go to 1.64.1.
        var outcome = Evaluate(Lookup(Docker("1.63.0", "1.65.1", "1.64.1"), NuGet("1.63.0", "1.64.1")), Config(OptedIn));

        Moves(outcome).Should().Equal((Image, "v1.64.1-noble"), (Package, "1.64.1"));
    }

    /// <summary>
    /// follower-cooldown-PRD FC6: a <c>version:</c> pattern may write a follower as <c>1.64</c>. Lockstep reads the
    /// follower's version from its candidate's token, the leader's (E2), so the follower agrees with the others.
    /// </summary>
    [Fact]
    public void A_follower_written_without_a_patch_still_agrees_with_its_leader()
    {
        var follower = Record(Image, "1.63", Candidate(EUpdateType.Minor, "v1.64.1", value: "1.64")) with
        {
            Ecosystem = "docker", Manager = "follow", Datasource = "docker", PackageFile = "apps/SeoTester/docker-compose.yml", LockFiles = [],
            DepType = null,
        };

        var outcome = Evaluate(Lookup(Docker("1.63.0", "1.65.1", "1.64.1"), NuGet("1.63.0", "1.64.1"), follower), Config(Followed));

        Moves(outcome).Should().Equal((Image, "v1.64.1-noble"), (Package, "1.64.1"), (Image, "1.64"));
    }

    [Fact]
    public void A_member_already_at_the_common_version_lets_the_others_catch_up()
    {
        var outcome = Evaluate(Lookup(Docker("1.63.0", "1.65.1", "1.64.1"), NuGet("1.64.1")), Config(OptedIn));

        Moves(outcome).Should().Equal((Image, "v1.64.1-noble"));
    }

    [Fact]
    public void Members_that_agree_move_as_they_would_without_lockstep()
    {
        var outcome = Evaluate(Lookup(Docker("1.64.1", "1.65.1"), NuGet("1.64.1", "1.65.1")), Config(OptedIn));

        Moves(outcome).Should().Equal((Image, "v1.65.1-noble"), (Package, "1.65.1"));
    }

    [Fact]
    public void When_the_members_first_offers_differ_the_highest_shared_version_wins()
    {
        // Lookup order puts 1.64.1 first for the image and 1.65.1 first for the package; both have both.
        var outcome = Evaluate(Lookup(Docker("1.63.0", "1.64.1", "1.65.1"), NuGet("1.63.0", "1.65.1", "1.64.1")), Config(OptedIn));

        Moves(outcome).Should().Equal((Image, "v1.65.1-noble"), (Package, "1.65.1"));
    }

    [Fact]
    public void A_tag_without_a_version_cannot_be_lockstepped_and_waits()
    {
        var image = Record(Image, "jammy", [Candidate(EUpdateType.Minor, "noble", value: "noble")]) with
        {
            Ecosystem = "docker", Manager = "dockerfile", Datasource = "docker", PackageFile = "apps/SeoTester/chromium/Dockerfile", LockFiles = [],
            DepType = null, CurrentVersion = null,
        };

        var outcome = Evaluate(Lookup(image, NuGet("1.64.1")), Config(OptedIn));

        outcome.Groups.Should().BeEmpty();
        outcome.Available.Should().Contain(e => e.Group == "playwright" && e.Reason == EDependencyReportReason.Lockstep
                                                && e.Detail == "lockstep: no member's version reads as x.y.z");
    }

    [Theory]
    [InlineData("v1.64.1-noble", "1.64.1")]
    [InlineData("11.0.0-rc.2-noble", "11.0.0-rc.2")]
    [InlineData("1.65.1", "1.65.1")]
    [InlineData("latest", null)]
    public void A_member_s_version_is_the_first_version_in_its_value(string value, string? expected)
        => DependencyFollowRule.DefaultVersion(value).Should().Be(expected);
}
