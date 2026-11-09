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
        pending.Detail.Should().Contain("Microsoft.AspNetCore.OpenApi").And.Contain("approve platform:dotnet 11.0 --force");
    }

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
