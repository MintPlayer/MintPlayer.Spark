using System.Text.Json;
using CodeCoverage.Dependencies.Lifecycle;
using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
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

    /// <param name="declared">#658: the action the apply workflow runs (the template's tag here) declares <c>sdk-preset</c>, so the
    /// plan commit also moves global.json to 11.0.100 and setup-dotnet installs it (SpaServices #111). Without it, an action
    /// from before #658 would refuse that plan commit, so the bot asks for the plan file only.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_release_of_the_rc_channel_is_planned_as_a_patch_with_its_companions_riding(bool declared)
    {
        var harness = new DependencyHarness(Store);
        harness.SetYml(DependencyHarness.Yml);
        harness.Updates.Files[$"{DependencyOnboarding.ActionRepository}@{DependencyOnboarding.ActionTag}:{DependencySdkPreset.ActionYmlPath}"] =
            declared ? "# capabilities: sdk-preset\nname: 'MintPlayer Dependency Update'\n" : "name: 'MintPlayer Dependency Update'\n";
        await harness.SeedAsync();
        await harness.StoreAsync(Released, Released.Id!);

        string table;
        using (var session = Store.OpenAsyncSession())
            table = await DependencyPlatformTable.JsonAsync(session, [], [], CancellationToken.None);
        using var parsed = JsonDocument.Parse(table);
        var row = parsed.RootElement.GetProperty("sdks").GetProperty("dotnet").EnumerateArray().Single();

        await harness.RunToApplyAsync(DependencyHarness.Lookup(OpenApi()) with { Platforms = [OnTheRc(row)] });

        // The lookup was handed the stored row: the table is the one source of the move (D29).
        var dispatched = harness.Queue.Dispatches[0];
        dispatched.Workflow.Should().Be("dependency-lookup.yml");
        dispatched.Inputs["platforms"].Should().Be(table);
        (row.GetProperty("latestSdk").GetString(), row.GetProperty("preview").GetBoolean()).Should().Be(("11.0.100", false));

        var planCommit = harness.Writer.Requests("work-ref").Should().ContainSingle().Which;
        planCommit.PresetSdk.Should().Be(declared);
        var plan = DependencyContract.Deserialize<DependencyPlan>(planCommit.PlanJson!)!;
        var group = plan.Groups.Should().ContainSingle().Which;
        group.Group.Should().Be("platform:dotnet 11.0");
        group.Platform!.FromChannel.Should().Be("11.0");
        group.Platform.ToChannel.Should().Be("11.0");
        group.Platform.ToVersion.Should().Be("11.0.0");
        group.Platform.SdkVersion.Should().Be("11.0.100");
        // D33: the RC companion rides to its release in the SDK's group, not in a group of its own.
        group.Items.Select(i => $"{i.DepName}@{i.NewVersion}").Should().Equal("Microsoft.AspNetCore.OpenApi@11.0.0");
    }

    /// <summary>
    /// follower-cooldown-PRD G2/M4: another repository of the owner publishes <c>Acme.Endpoints</c> (its manifest, the
    /// producer graph); the widget's reference to it, on the platform's major, rides to its release with the SDK. The
    /// widget's own manifest publishes <c>Acme.Widget</c>, which stays an ordinary package here.
    /// </summary>
    [Fact]
    public async Task An_upstream_package_of_the_owner_rides_with_the_platform_move()
    {
        var harness = new DependencyHarness(Store);
        harness.SetYml(DependencyHarness.Yml);
        await harness.SeedAsync();
        await harness.StoreAsync(Released, Released.Id!);
        var upstreamId = Repository.DocumentId(EForgeProvider.GitHub, 9101);
        await harness.StoreAsync(new Repository
        {
            GitHubId = 9101, Name = "tools", FullName = "acme/tools", OwnerLogin = "acme", DefaultBranch = "master",
            Account = DependencyHarness.AccountId, Provider = EForgeProvider.GitHub,
        }, upstreamId);
        await harness.StoreAsync(new RepositoryManifest
        {
            Repository = upstreamId, OwnerKey = new ForgeOwner(EForgeProvider.GitHub, "acme").ToString(), Produces = [new ManifestPackage { Ecosystem = "NuGet", Name = "Acme.Endpoints" }],
        }, RepositoryManifest.ForRepository(upstreamId));
        WaitForIndexing(Store);

        string table;
        using (var session = Store.OpenAsyncSession())
            table = await DependencyPlatformTable.JsonAsync(session, [], [], CancellationToken.None);
        using var parsed = JsonDocument.Parse(table);
        var row = parsed.RootElement.GetProperty("sdks").GetProperty("dotnet").EnumerateArray().Single();
        LookupRecord Package(string name, string current, string next) => DependencyTestKit.Record(name, current,
            DependencyTestKit.Candidate(EUpdateType.Patch, next)) with
        { Ecosystem = "nuget", Manager = "nuget", Datasource = "nuget", PackageFile = "src/Web/Web.csproj", LockFiles = [] };

        await harness.RunToApplyAsync(DependencyHarness.Lookup(Package("Acme.Endpoints", "11.4.1-rc.0", "11.4.1")) with { Platforms = [OnTheRc(row)] });

        var plan = DependencyContract.Deserialize<DependencyPlan>(harness.Writer.Requests("work-ref").Single().PlanJson!)!;
        plan.Groups.Should().ContainSingle().Which.Items.Select(i => $"{i.DepName}@{i.NewVersion}").Should().Equal("Acme.Endpoints@11.4.1");
    }

    /// <summary>
    /// prerelease-track-scenarios-PRD D5 (Tools #218, 2026-11-15): the apply refused the platform group with NU1605 on
    /// <c>Microsoft.Extensions.DependencyInjection</c>, so R2 held it on that blocker. A fixed engine now plans the blocker
    /// in the group, but the lookup did not change, so the hold stood and the fix never reached the pull request. The
    /// hold binds to the items it refused: this plan is another, so it lifts, the group is planned with both packages at
    /// 11.0.0, and the hold the next refusal takes records its items.
    /// </summary>
    [Fact]
    public async Task A_platform_hold_from_before_a_fix_lifts_when_the_group_is_planned_with_its_blocker()
    {
        var harness = new DependencyHarness(Store);
        harness.SetYml(DependencyHarness.Yml);
        await harness.SeedAsync();
        await harness.StoreAsync(Released, Released.Id!);
        await harness.StoreAsync(new DependencyUpdateState
        {
            Repository = DependencyHarness.RepositoryId,
            Holds =
            [
                new DependencyHold
                {
                    Id = "blocker:platform:dotnet 11.0", Kind = EDependencyHoldKind.Blocker, Group = "platform:dotnet 11.0", DepNames = ["platform:dotnet"],
                    Reason = "the resolver refused it: blocked by Microsoft.Extensions.DependencyInjection (R2)",
                    Blockers = [new DependencyBlockerSeen { DepName = "Microsoft.Extensions.DependencyInjection", SeenVersions = [RcPackage, "11.0.0"], CurrentTokens = [RcPackage] }],
                    CreatedAtUtc = new DateTime(2026, 11, 15, 20, 0, 0, DateTimeKind.Utc),
                },
            ],
        }, DependencyUpdateState.ForRepository(DependencyHarness.RepositoryId));

        string table;
        using (var session = Store.OpenAsyncSession())
            table = await DependencyPlatformTable.JsonAsync(session, [], [], CancellationToken.None);
        using var parsed = JsonDocument.Parse(table);
        var row = parsed.RootElement.GetProperty("sdks").GetProperty("dotnet").EnumerateArray().Single();
        LookupRecord Package(string name, string current, string next, EUpdateType type) => DependencyTestKit.Record(name, current,
            DependencyTestKit.Candidate(type, next)) with
        { Ecosystem = "nuget", Manager = "nuget", Datasource = "nuget", PackageFile = "src/Web/Web.csproj", LockFiles = [] };

        await harness.RunToApplyAsync(DependencyHarness.Lookup(
            Package("Microsoft.Extensions.Hosting", "10.0.5", "11.0.0", EUpdateType.Major),
            Package("Microsoft.Extensions.DependencyInjection", RcPackage, "11.0.0", EUpdateType.Patch)) with { Platforms = [OnTheRc(row)] });

        var plan = DependencyContract.Deserialize<DependencyPlan>(harness.Writer.Requests("work-ref").Single().PlanJson!)!;
        var group = plan.Groups.Should().ContainSingle().Which;
        group.Group.Should().Be("platform:dotnet 11.0");
        group.Items.Select(i => $"{i.DepName}@{i.NewVersion}").Order(StringComparer.Ordinal)
            .Should().Equal("Microsoft.Extensions.DependencyInjection@11.0.0", "Microsoft.Extensions.Hosting@11.0.0");
        (await harness.StateAsync()).Holds.Should().NotContain(h => h.Group == "platform:dotnet 11.0" && h.Refused.Count == 0);
    }
}
