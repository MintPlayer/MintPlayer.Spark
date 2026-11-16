using CodeCoverage.Dependencies;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// #617 (docs/code-coverage/sdk-updates-PRD.md §12): the platform table's row for the channel a repository is on is the
/// move inside it. A repository on the 11.0 RC is offered 11.0's release as the <c>platform:dotnet 11.0</c> group (D29), a
/// patch under the account's rules (D32), with the RC companions riding to their release (D33).
/// </summary>
public class PlatformInChannelMoveTests
{
    private const string RcSdk = "11.0.100-rc.2.26504.105";
    private static readonly DateTimeOffset Later = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlatformCandidate Row(string channel, string version, string sdk, bool preview = false, bool? lts = false, string released = "2026-11-10")
        => new() { Channel = channel, LatestVersion = version, LatestSdk = sdk, ReleaseTimestamp = released, Preview = preview, Lts = lts };

    private static PlatformRecord Dotnet(string channel, string current, bool preview, params PlatformCandidate[] candidates) => new()
    {
        Sdk = "dotnet",
        Source = "releases-index",
        Current = new PlatformCurrent { Channels = [channel], Preview = preview },
        Locations =
        [
            new PlatformLocation { File = "global.json", Kind = "global.json", Value = current, Channel = channel },
            new PlatformLocation { File = ".github/workflows/ci.yml", Kind = "setup-action", Value = current, Channel = channel },
            new PlatformLocation { File = ".github/workflows/ci.yml", Kind = "dotnet-quality", Value = "preview", Channel = channel },
        ],
        Candidates = [.. candidates],
    };

    /// <summary>The 11.0 RC on the day 11.0 is released.</summary>
    private static PlatformRecord OnTheRc(string released = "2026-11-10") => Dotnet("11.0", RcSdk, preview: true, Row("11.0", "11.0.0", "11.0.100", released: released));

    private static LookupRecord Nuget(string depName, string current, params LookupCandidate[] candidates)
        => Record(depName, current, candidates) with { Ecosystem = "nuget", Manager = "nuget", Datasource = "nuget", PackageFile = "src/Web/Web.csproj", LockFiles = [] };

    private static LookupDocument With(PlatformRecord platform, params LookupRecord[] records) => Lookup(records) with { Platforms = [platform] };

    [Fact]
    public void The_release_of_the_rc_channel_is_a_patch_platform_group()
    {
        var outcome = Evaluate(With(OnTheRc()), now: Later);

        var group = outcome.Groups.Should().ContainSingle().Which;
        group.Group.Should().Be("platform:dotnet 11.0");
        group.Level.Should().Be(EUpdateLevel.Patch);
        group.TargetMajors.Should().Equal("platform:dotnet@11.0.100");
        group.Platform!.FromChannel.Should().Be("11.0");
        group.Platform.ToChannel.Should().Be("11.0");
        group.Platform.ToVersion.Should().Be("11.0.0");
        group.Platform.SdkVersion.Should().Be("11.0.100");
    }

    [Fact]
    public void The_rc_companions_ride_to_their_release()
    {
        var openApi = Nuget("Microsoft.AspNetCore.OpenApi", "11.0.0-rc.2.26504.105", Candidate(EUpdateType.Patch, "11.0.0"));

        var outcome = Evaluate(With(OnTheRc(), openApi), now: Later);

        Planned(outcome).Should().Equal("platform:dotnet 11.0: Microsoft.AspNetCore.OpenApi@11.0.0");
    }

    [Fact]
    public void An_rc_companion_without_its_release_holds_the_move_until_forced()
    {
        // Only the next RC is out: the companion cannot ride to the release, so the move waits (D3), as a major move does.
        var openApi = Nuget("Microsoft.AspNetCore.OpenApi", "11.0.0-rc.2.26504.105", Candidate(EUpdateType.Patch, "11.0.0-rc.3.26522.148"));

        var outcome = Evaluate(With(OnTheRc(), openApi), now: Later);

        outcome.Groups.Should().NotContain(g => g.Group == "platform:dotnet 11.0");
        var pending = outcome.Held.Concat(outcome.Available).Should().ContainSingle(e => e.Reason == EDependencyReportReason.Pending).Which;
        pending.Detail.Should().Contain("Microsoft.AspNetCore.OpenApi").And.Contain("/approve platform:dotnet 11.0 --force");
    }

    /// <summary>
    /// follower-cooldown-PRD FS4/G3 (S2): a project that targets only .NET Standard (Spark's source generators) has no
    /// platform to move; its <c>tfm</c> location carries no channel. Its references are ordinary packages: they neither ride
    /// with nor hold the platform move. A file with no <c>tfm</c> location (Directory.Packages.props) can't be told and
    /// stays a companion.
    /// </summary>
    [Fact]
    public void A_dotnet_standard_projects_reference_neither_rides_nor_holds_the_platform_move()
    {
        const string generator = "libs/Generators/Generators.csproj";
        var platform = OnTheRc() with
        {
            Locations = [.. OnTheRc().Locations, new PlatformLocation { File = generator, Kind = "tfm", Value = "netstandard2.0", Channel = null }],
        };
        var json = Nuget("System.Text.Json", "11.0.0-rc.2.26504.105", Candidate(EUpdateType.Patch, "11.0.0-rc.3.26522.148")) with { PackageFile = generator };

        var outcome = Evaluate(With(platform, json), now: Later);

        outcome.Groups.Should().Contain(g => g.Group == "platform:dotnet 11.0");
        outcome.Groups.Single(g => g.Group == "platform:dotnet 11.0").Items.Should().BeEmpty();
        outcome.Held.Concat(outcome.Available).Should().NotContain(e => e.Reason == EDependencyReportReason.Pending);
    }

    /// <summary>
    /// follower-cooldown-PRD G2 (owner, 2026-11-15: "MintPlayer.Dotnet.Tools is the first one that will need to be
    /// updated. The other repos will follow suit"): a package another repository of the account publishes, on the
    /// platform's major, rides with the move like a Microsoft one (S3: Spark's <c>MintPlayer.AspNetCore.Endpoints</c>).
    /// </summary>
    [Fact]
    public void An_upstream_package_of_the_account_rides_to_its_release()
    {
        var endpoints = Nuget("MintPlayer.AspNetCore.Endpoints", "11.4.1-rc.0", Candidate(EUpdateType.Patch, "11.4.1"));

        var outcome = Evaluate(With(OnTheRc(), endpoints), Upstream(), now: Later);

        Planned(outcome).Should().Equal("platform:dotnet 11.0: MintPlayer.AspNetCore.Endpoints@11.4.1");
    }

    /// <summary>
    /// follower-cooldown-PRD G2: the downstream repository waits for its upstream's release (S3: SpaServices has only
    /// rc.6 on 2026-11-15), as it waits for a Microsoft companion's; a writer can still force the move.
    /// </summary>
    [Fact]
    public void An_upstream_package_without_its_release_holds_the_move_until_forced()
    {
        var spa = Nuget("MintPlayer.AspNetCore.SpaServices", "11.0.0-rc.3", Candidate(EUpdateType.Patch, "11.0.0-rc.6"));

        var outcome = Evaluate(With(OnTheRc(), spa), Upstream(), now: Later);

        outcome.Groups.Should().NotContain(g => g.Group == "platform:dotnet 11.0");
        outcome.Held.Concat(outcome.Available).Should().ContainSingle(e => e.Reason == EDependencyReportReason.Pending)
            .Which.Detail.Should().Contain("MintPlayer.AspNetCore.SpaServices");
    }

    [Fact]
    public void Companions_off_leaves_the_upstream_packages_alone_too()
    {
        var endpoints = Nuget("MintPlayer.AspNetCore.Endpoints", "11.4.1-rc.0", Candidate(EUpdateType.Patch, "11.4.1"));

        var outcome = Evaluate(With(OnTheRc(), endpoints), Upstream("dependencies:\n  platforms:\n    dotnet: { companions: [] }\n"), now: Later);

        outcome.Groups.Single(g => g.Group == "platform:dotnet 11.0").Items.Should().BeEmpty();
    }

    /// <summary>
    /// follower-cooldown-PRD H1 (owner, 2026-11-15, Dotnet.Tools #215 review: "Why not 11.x.x"): a project that targets only
    /// the platform's channel (<c>net11.0</c>) takes the channel's major of its platform packages. A 10.0.5 reference there
    /// rides to 11.0.0 in the platform group, not to 10.0.15 with 11.0.0 awaiting approval.
    /// </summary>
    [Fact]
    public void A_net11_projects_older_platform_package_rides_to_the_channels_major()
    {
        var di = OnNet11(Nuget("Microsoft.Extensions.DependencyInjection", "10.0.5", Candidate(EUpdateType.Patch, "10.0.15"), Candidate(EUpdateType.Major, "11.0.0")));

        var outcome = Evaluate(With(OnTheRc().WithTfm(Web, "net11.0"), di), now: Later);

        Planned(outcome).Should().Equal("platform:dotnet 11.0: Microsoft.Extensions.DependencyInjection@11.0.0");
        outcome.AwaitingApproval.Should().BeEmpty();
    }

    /// <summary>H1 (owner, #215: "Why not 11.0.0?" on <c>dotnet-ef</c> 8.0.3 → 8.0.34): a tool manifest runs on the SDK, so its
    /// platform tool takes the SDK channel's major.</summary>
    [Fact]
    public void A_tool_manifests_platform_tool_rides_to_the_channels_major()
    {
        var ef = Nuget("dotnet-ef", "8.0.3", Candidate(EUpdateType.Patch, "8.0.34"), Candidate(EUpdateType.Major, "11.0.0")) with { PackageFile = "src/Web/.config/dotnet-tools.json" };

        var outcome = Evaluate(With(OnTheRc().WithTfm(Web, "net11.0"), ef), now: Later);

        Planned(outcome).Should().Equal("platform:dotnet 11.0: dotnet-ef@11.0.0");
        outcome.AwaitingApproval.Should().BeEmpty();
    }

    /// <summary>H1: with no platform move to ride (the channel is already released), the alignment is a group of its own.</summary>
    [Fact]
    public void On_a_released_channel_an_older_platform_package_still_aligns()
    {
        var platform = Dotnet("11.0", "11.0.100", preview: false).WithTfm(Web, "net11.0");
        var di = OnNet11(Nuget("Microsoft.Extensions.DependencyInjection", "10.0.5", Candidate(EUpdateType.Patch, "10.0.15"), Candidate(EUpdateType.Major, "11.0.0")));

        var outcome = Evaluate(With(platform, di), now: Later);

        Planned(outcome).Should().Equal("platform:dotnet 11.0: Microsoft.Extensions.DependencyInjection@11.0.0");
        outcome.AwaitingApproval.Should().BeEmpty();
    }

    /// <summary>H1 (Tools #218, 2026-11-15): CliCommandDebugging (<c>net11.0</c> only) had Hosting 10.0.5 and DependencyInjection
    /// 11.0.0-rc.1. Hosting rode to 11.0.0 in the platform group; the rc, on the channel's major but no release, was neither
    /// "on the major" nor "behind" it, so it stayed, and restore refused the downgrade (Hosting 11.0.0 needs DI ≥ 11.0.0, NU1605).
    /// On a released channel, a prerelease of the channel's major is behind the release and rides to it beside them.</summary>
    [Fact]
    public void On_a_released_channel_a_prerelease_of_its_major_rides_to_the_release()
    {
        var platform = Dotnet("11.0", "11.0.100", preview: false, Row("11.0", "11.0.1", "11.0.101")).WithTfm(Web, "net11.0");
        var hosting = OnNet11(Nuget("Microsoft.Extensions.Hosting", "10.0.5", Candidate(EUpdateType.Patch, "10.0.15"), Candidate(EUpdateType.Major, "11.0.0")));
        var di = OnNet11(Nuget("Microsoft.Extensions.DependencyInjection", "11.0.0-rc.1.26425.128", Candidate(EUpdateType.Patch, "11.0.0")));

        var outcome = Evaluate(With(platform, hosting, di), now: Later);

        outcome.Groups.Should().ContainSingle();
        Planned(outcome).Should().Equal("platform:dotnet 11.0: Microsoft.Extensions.Hosting@11.0.0, Microsoft.Extensions.DependencyInjection@11.0.0");
    }

    /// <summary>multi-target-dependency-split-PRD D-b (supersedes the H1 boundary "a multi-target project's older platform
    /// package keeps its major"): the project keeps building for net10.0 with 10.0.15 and takes 11.0.0 for net11.0, as one
    /// split item in the platform group, so nothing waits for approval.</summary>
    [Fact]
    public void A_multi_target_projects_platform_package_splits_per_TFM()
    {
        var di = OnNet11(Nuget("Microsoft.Extensions.DependencyInjection", "10.0.5", Candidate(EUpdateType.Patch, "10.0.15"), Candidate(EUpdateType.Major, "11.0.0")));

        var outcome = Evaluate(With(OnTheRc().WithTfm(Web, "net10.0;net11.0"), di), now: Later);

        var item = outcome.Groups.Single(g => g.Group == "platform:dotnet 11.0").Items.Single();
        string.Join(", ", item.Layout!.Select(l => $"{l.TargetFramework}={l.Version}")).Should().Be("net10.0=10.0.15, net11.0=11.0.0");
        outcome.AwaitingApproval.Should().BeEmpty();
    }

    /// <summary>H1 boundary: a <c>System.*</c> package that is no platform package (no release on the channel's major) is
    /// left to its own group.</summary>
    [Fact]
    public void A_package_with_no_release_on_the_channels_major_is_not_aligned()
    {
        var cli = OnNet11(Nuget("System.CommandLine", "2.0.5", Candidate(EUpdateType.Patch, "2.0.15"), Candidate(EUpdateType.Major, "3.0.0")));

        var outcome = Evaluate(With(OnTheRc().WithTfm(Web, "net11.0"), cli), now: Later);

        Planned(outcome).Should().Contain("System.CommandLine: System.CommandLine@2.0.15");
        outcome.Groups.Single(g => g.Group == "platform:dotnet 11.0").Items.Should().BeEmpty();
    }

    private const string Web = "src/Web/Web.csproj";

    private static LookupRecord OnNet11(LookupRecord record) => record with { PackageFile = Web };

    /// <summary>The account publishes Endpoints and SpaServices from other repositories (the producer graph).</summary>
    private static DependencyPolicyConfig Upstream(string? yml = null) => DependencyPolicyConfig.Resolve(Config(yml).File, null, null,
        [DependencyPolicyConfig.AccountPackageKey("nuget", "MintPlayer.AspNetCore.Endpoints"), DependencyPolicyConfig.AccountPackageKey("nuget", "MintPlayer.AspNetCore.SpaServices")]);

    [Fact]
    public void A_patch_between_two_releases_carries_no_companions()
    {
        var platform = Dotnet("10.0", "10.0.402", preview: false, Row("10.0", "10.0.14", "10.0.403", lts: true));
        var extensions = Nuget("Microsoft.Extensions.Hosting", "10.0.13", Candidate(EUpdateType.Patch, "10.0.14"));

        var outcome = Evaluate(With(platform, extensions), now: Later);

        Planned(outcome).Order(StringComparer.Ordinal).Should().Equal("Microsoft.Extensions.Hosting: Microsoft.Extensions.Hosting@10.0.14", "platform:dotnet 10.0: ");
        outcome.Groups.Single(g => g.Group == "platform:dotnet 10.0").Level.Should().Be(EUpdateLevel.Patch);
    }

    [Fact]
    public void The_cooldown_names_the_version_inside_the_channel()
    {
        var outcome = Evaluate(With(OnTheRc(released: "2026-11-30")), now: Later);

        outcome.Groups.Should().BeEmpty();
        outcome.Available.Should().ContainSingle(e => e.Reason == EDependencyReportReason.Cooldown).Which.Target.Should().Be("11.0.100");
    }

    [Fact]
    public void An_lts_track_still_takes_the_patches_of_the_sts_channel_it_is_on()
    {
        var platform = Dotnet("9.0", "9.0.318", preview: false, Row("10.0", "10.0.14", "10.0.403", lts: true), Row("9.0", "9.0.21", "9.0.319"));
        var yml = "dependencies:\n  platforms:\n    dotnet: { track: lts }\n";

        var outcome = Evaluate(With(platform), Config(yml), now: Later);

        // 10.0 is a minor channel move held for approval (R7); 9.0.319 is the patch of the channel the repository is on.
        outcome.Groups.Select(g => g.Group).Should().Contain("platform:dotnet 9.0");
    }

    [Fact]
    public void A_newer_rc_inside_the_channel_is_a_preview_the_stable_track_does_not_follow()
    {
        var platform = Dotnet("11.0", RcSdk, preview: true, Row("11.0", "11.0.0-rc.3", "11.0.100-rc.3.26522.148", preview: true));

        var outcome = Evaluate(With(platform), now: Later);

        outcome.Groups.Should().BeEmpty();
        outcome.Available.Should().ContainSingle(e => e.Reason == EDependencyReportReason.PreviewNotFollowed).Which.Target.Should().Be("11.0.100-rc.3.26522.148");
    }

    [Fact]
    public void An_approval_of_the_channel_move_does_not_carry_over_to_the_patch()
    {
        var outcome = Evaluate(With(OnTheRc()), now: Later);

        outcome.Groups.Single().TargetMajors.Should().NotContain("platform:dotnet@11");
    }
}

internal static class PlatformRecordTfm
{
    /// <summary>The platform with a project's <c>tfm</c> location added (the lookup's scan of a csproj).</summary>
    public static PlatformRecord WithTfm(this PlatformRecord platform, string file, string value) => platform with
    {
        Locations = [.. platform.Locations, new PlatformLocation { File = file, Kind = "tfm", Value = value, Channel = "11.0" }],
    };
}
