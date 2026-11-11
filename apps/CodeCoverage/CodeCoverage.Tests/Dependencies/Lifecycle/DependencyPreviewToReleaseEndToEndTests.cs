using System.Text.Json;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.Tests.Dependencies.Policy;
using MintPlayer.Spark.Testing;
using Xunit;

namespace CodeCoverage.Tests.Dependencies.Lifecycle;

/// <summary>
/// #617 end to end on <see cref="SparkTestDriver"/> (owner request 2026-11-09, docs/code-coverage/sdk-updates-PRD.md §12
/// M1): the stored <see cref="PlatformRelease"/> rows the collector writes, the <c>platforms</c> input the lookup dispatch
/// carries, the platform record the action builds from it (as previewToGa.test.ts proves on the action's side), and the
/// plan the saga commits. A repository on the 11.0 RC, the day 11.0 is released: the plan moves it to the release in the
/// <c>platform:dotnet 11.0</c> group, its RC companion riding to 11.0.0.
/// <para>Red before #617 M3: the in-channel candidate was a minor channel "move" to itself with no riders, so the RC
/// package stayed in a group of its own and the SDK moved without it.</para>
/// </summary>
public class DependencyPreviewToReleaseEndToEndTests : SparkTestDriver
{
    // Like CoverageRavenTest: fork pull requests get no licence, and nothing here needs a licensed feature.
    protected override bool RequireLicense => false;

    private const string RcSdk = "11.0.100-rc.2.26504.105";
    private const string RcPackage = "11.0.0-rc.2.26504.105";

    private static readonly PlatformRelease Released = new()
    {
        Id = PlatformRelease.IdFor("dotnet", "11.0"), Platform = "dotnet", Channel = "11.0", Major = 11, Name = "11.0", Phase = "active",
        LatestVersion = "11.0.0", LatestSdk = "11.0.100", LatestReleasedOn = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        SupportEndsOn = new DateTime(2028, 5, 9, 0, 0, 0, DateTimeKind.Utc), Source = "releases-index",
    };

    /// <summary>What the action builds from the table row of the channel the repository is on (feeds.ts buildPlatformRecord).</summary>
    private static PlatformRecord OnTheRc(JsonElement row) => new()
    {
        Sdk = "dotnet",
        Source = "releases-index",
        Current = new PlatformCurrent { Channels = ["11.0"], Preview = true },
        Locations =
        [
            new PlatformLocation { File = "global.json", Kind = "global.json", Value = RcSdk, Channel = "11.0" },
            new PlatformLocation { File = ".github/workflows/ci.yml", Kind = "setup-action", Value = RcSdk, Channel = "11.0" },
            new PlatformLocation { File = ".github/workflows/ci.yml", Kind = "dotnet-quality", Value = "preview", Channel = "11.0" },
            new PlatformLocation { File = "src/Web/Web.csproj", Kind = "tfm", Value = "net11.0", Channel = "11.0" },
        ],
        Candidates =
        [
            new PlatformCandidate
            {
                Channel = row.GetProperty("channel").GetString()!, LatestVersion = row.GetProperty("latestVersion").GetString(),
                LatestSdk = row.GetProperty("latestSdk").GetString(), ReleaseTimestamp = row.GetProperty("releaseTimestamp").GetString(),
                Preview = row.GetProperty("preview").GetBoolean(), Lts = row.GetProperty("lts").GetBoolean(),
            },
        ],
    };

    private static LookupRecord OpenApi() => DependencyTestKit.Record("Microsoft.AspNetCore.OpenApi", RcPackage,
        DependencyTestKit.Candidate(EUpdateType.Patch, "11.0.0")) with
    {
        Ecosystem = "nuget", Manager = "nuget", Datasource = "nuget", PackageFile = "src/Web/Web.csproj", LockFiles = [],
    };

    [Fact]
    public async Task The_release_of_the_rc_channel_is_planned_as_a_patch_with_its_companions_riding()
    {
        var harness = new DependencyHarness(Store);
        harness.SetYml(DependencyHarness.Yml);
        await harness.SeedAsync();
        await harness.StoreAsync(Released, Released.Id!);

        string table;
        using (var session = Store.OpenAsyncSession())
            table = await DependencyPlatformTable.JsonAsync(session, [], CancellationToken.None);
        using var parsed = JsonDocument.Parse(table);
        var row = parsed.RootElement.GetProperty("sdks").GetProperty("dotnet").EnumerateArray().Single();

        await harness.RunToApplyAsync(DependencyHarness.Lookup(OpenApi()) with { Platforms = [OnTheRc(row)] });

        // The lookup was handed the stored row: the table is the one source of the move (D29).
        var dispatched = harness.Queue.Dispatches[0];
        dispatched.Workflow.Should().Be("dependency-lookup.yml");
        dispatched.Inputs["platforms"].Should().Be(table);
        (row.GetProperty("latestSdk").GetString(), row.GetProperty("preview").GetBoolean()).Should().Be(("11.0.100", false));

        var plan = DependencyContract.Deserialize<DependencyPlan>(harness.Writer.Requests("work-ref").Should().ContainSingle().Which.PlanJson!)!;
        var group = plan.Groups.Should().ContainSingle().Which;
        group.Group.Should().Be("platform:dotnet 11.0");
        group.Platform!.FromChannel.Should().Be("11.0");
        group.Platform.ToChannel.Should().Be("11.0");
        group.Platform.ToVersion.Should().Be("11.0.0");
        group.Platform.SdkVersion.Should().Be("11.0.100");
        // D33: the RC companion rides to its release in the SDK's group, not in a group of its own.
        group.Items.Select(i => $"{i.DepName}@{i.NewVersion}").Should().Equal("Microsoft.AspNetCore.OpenApi@11.0.0");
    }
}
