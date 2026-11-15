using CodeCoverage.Dependencies.Policy;
using CodeCoverage.Git.Dependencies;
using CodeCoverage.LookupReferences;
using Xunit;
using static CodeCoverage.Tests.Dependencies.Policy.DependencyTestKit;

namespace CodeCoverage.Tests.Dependencies.Policy;

/// <summary>
/// companion-version-maps-PRD M2 (§6.2, §6.3; owner, 2026-11-15: "let the relations between the SDK version ... and some
/// package versions ... specified in the coverage.yml file too? Then the bot knows what package versions it should pick"): a
/// companion entry's range, keyed by the repository's SDK band or the project's channel, is the ceiling of an ordinary update
/// and the target of an alignment; a platform move recomputes it for the target SDK and never waits on it.
/// </summary>
public class CompanionVersionMapTests
{
    private static readonly DateTimeOffset Later = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Web = "src/Web/Web.csproj";

    private static PlatformRecord Dotnet(string channel, string sdk, string tfm, params PlatformCandidate[] candidates) => new()
    {
        Sdk = "dotnet",
        Source = "releases-index",
        Current = new PlatformCurrent { Channels = [channel] },
        Locations =
        [
            new PlatformLocation { File = "global.json", Kind = "global.json", Value = sdk, Channel = channel },
            new PlatformLocation { File = Web, Kind = "tfm", Value = tfm, Channel = channel },
        ],
        Candidates = [.. candidates],
    };

    private static PlatformCandidate Row(string channel, string version, string sdk)
        => new() { Channel = channel, LatestVersion = version, LatestSdk = sdk, ReleaseTimestamp = "2026-11-10", Lts = channel == "10.0" };

    private static LookupRecord Nuget(string depName, string current, params LookupCandidate[] candidates)
        => Record(depName, current, candidates) with { Ecosystem = "nuget", Manager = "nuget", Datasource = "nuget", PackageFile = Web, LockFiles = [] };

    private static DependencyPolicyOutcome Run(PlatformRecord platform, LookupRecord record, string? versions = null, params SdkComponentVersions[] components)
    {
        var yml = versions is null ? null
            : $"dependencies:\n  platforms:\n    dotnet:\n      companions:\n        - match: \"Microsoft.CodeAnalysis*\"\n          versions: {versions}\n        - builtin\n";
        return Evaluate(Lookup([record]) with { Platforms = [platform] }, Config(yml), now: Later, components: components);
    }

    private static LookupRecord Roslyn(string current, params string[] minors)
        => Nuget("Microsoft.CodeAnalysis.CSharp", current, [.. minors.Select(v => Candidate(EUpdateType.Minor, v))]);

    /// <summary>S3: an npm range over NuGet's versions. A prerelease build (<c>18.12.0-1.26473.115</c>) is admitted only by a
    /// range naming that prerelease line (npm's rule), and a four-part version is no SemVer, so no range admits it.</summary>
    [Theory]
    [InlineData("<=18.12", "18.12.2", true)]
    [InlineData("<=18.12", "18.13.0", false)]
    [InlineData("<=18.12", "18.12.0-1.26473.115", false)]
    [InlineData("5.12.x", "5.12.0", true)]
    [InlineData("<=5.12", "4.0.0.1", false)]
    public void S3_npm_ranges_over_nuget_versions(string range, string version, bool admitted)
    {
        CodeCoverage.Versioning.Versions.NpmRange.TryParse(range, out var parsed).Should().BeTrue();
        parsed.Admits(version).Should().Be(admitted);
    }

    [Fact]
    public void A_range_is_the_ceiling_of_an_ordinary_update()
    {
        var outcome = Run(Dotnet("11.0", "11.0.100", "net11.0"), Roslyn("5.9.0", "5.14.0"), "{ \"11.0\": \"<=5.12\" }");

        Planned(outcome).Should().NotContain(p => p.Contains("5.14.0"));
        outcome.Available.Should().ContainSingle(e => e.DepName == "Microsoft.CodeAnalysis.CSharp" && e.Reason == EDependencyReportReason.AboveCompanionRange)
            .Which.Detail.Should().Contain("<=5.12").And.Contain("11.0");
    }

    [Fact]
    public void Inside_the_range_the_update_is_planned()
    {
        var outcome = Run(Dotnet("11.0", "11.0.100", "net11.0"), Roslyn("5.9.0", "5.12.0"), "{ \"11.0\": \"<=5.12\" }");

        Planned(outcome).Should().ContainSingle(p => p.EndsWith("Microsoft.CodeAnalysis.CSharp@5.12.0", StringComparison.Ordinal));
    }

    [Fact]
    public void The_band_of_the_repositorys_sdk_beats_the_channel()
    {
        var outcome = Run(Dotnet("11.0", "11.0.100", "net11.0"), Roslyn("5.9.0", "5.12.0"), "{ \"11.0\": \"<=5.9\", \"11.0.1xx\": \"<=5.12\" }");

        Planned(outcome).Should().ContainSingle(p => p.EndsWith("Microsoft.CodeAnalysis.CSharp@5.12.0", StringComparison.Ordinal));
    }

    /// <summary>multi-target-dependency-split-PRD S6 (supersedes "a multi-target project takes its lowest channel's range"):
    /// each TFM takes the range keyed by its own channel, so net10.0 keeps 5.0.0 and net11.0 moves to 5.12.0, as one split item
    /// in the platform group. A ranged package's majors are its own (Roslyn 5.x on .NET 11), not the channel's.</summary>
    [Fact]
    public void A_multi_target_project_takes_each_channels_range()
    {
        var outcome = Run(Dotnet("11.0", "11.0.100", "net10.0;net11.0"), Roslyn("5.0.0", "5.12.0"), "{ \"10.0\": \"<=5.0\", \"11.0\": \"<=5.12\" }");

        var item = outcome.Groups.Single(g => g.Group == "platform:dotnet 11.0").Items.Single();
        string.Join(", ", item.Layout!.Select(l => $"{l.TargetFramework}={l.Version}")).Should().Be("net10.0=5.0.0, net11.0=5.12.0");
    }

    [Fact]
    public void A_record_below_the_range_aligns_in_the_platform_group()
    {
        var outcome = Run(Dotnet("11.0", "11.0.100", "net11.0"), Roslyn("5.9.0", "5.12.0"), "{ \"11.0\": \"5.12.x\" }");

        Planned(outcome).Should().Equal("platform:dotnet 11.0: Microsoft.CodeAnalysis.CSharp@5.12.0");
        outcome.AwaitingApproval.Should().BeEmpty();
    }

    [Fact]
    public void A_key_that_does_not_apply_leaves_an_ordinary_package()
    {
        var outcome = Run(Dotnet("11.0", "11.0.100", "net11.0"), Roslyn("5.9.0", "5.14.0"), "{ \"10.0\": \"<=5.0\" }");

        Planned(outcome).Should().ContainSingle(p => p.EndsWith("Microsoft.CodeAnalysis.CSharp@5.14.0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bundled_companion_stays_at_or_below_what_the_sdk_ships()
    {
        var msbuild = Nuget("Microsoft.Build", "18.4.0", Candidate(EUpdateType.Minor, "18.14.0"));

        var outcome = Run(Dotnet("11.0", "11.0.100", "net11.0"), msbuild, null, new SdkComponentVersions("11.0.1xx", "18.12.2", "5.12.0", "8.0.0"));

        Planned(outcome).Should().NotContain(p => p.Contains("Microsoft.Build"));
        outcome.Available.Should().ContainSingle(e => e.DepName == "Microsoft.Build" && e.Reason == EDependencyReportReason.AboveCompanionRange)
            .Which.Detail.Should().Contain("<=18.12").And.Contain("11.0.100");
    }

    [Fact]
    public void Without_the_sdks_components_a_bundled_companion_is_an_ordinary_package()
    {
        var msbuild = Nuget("Microsoft.Build", "18.4.0", Candidate(EUpdateType.Minor, "18.14.0"));

        var outcome = Run(Dotnet("11.0", "11.0.100", "net11.0"), msbuild);

        Planned(outcome).Should().ContainSingle(p => p.EndsWith("Microsoft.Build@18.14.0", StringComparison.Ordinal));
    }

    /// <summary>§6.3: a move to a new band recomputes the range for the target SDK; the companion rides to the release inside
    /// it (SDK 10.0.100 ships MSBuild 18.0.2, 10.0.300 ships 18.6.3: S1).</summary>
    [Fact]
    public void A_platform_move_recomputes_the_range_for_the_target_sdk()
    {
        var msbuild = Nuget("Microsoft.Build", "18.0.2", Candidate(EUpdateType.Minor, "18.6.3"));

        var outcome = Run(Dotnet("10.0", "10.0.100", "net10.0", Row("10.0", "10.0.5", "10.0.300")), msbuild, null,
            new SdkComponentVersions("10.0.1xx", "18.0.2", "5.0.0", "7.0.0"), new SdkComponentVersions("10.0.3xx", "18.6.3", "5.6.0", "7.6.0"));

        Planned(outcome).Should().Equal("platform:dotnet 10.0: Microsoft.Build@18.6.3");
    }

    /// <summary>§6.5 (F-C: until now a coverage.yml error reached the run's timeline only, and the whole section silently fell
    /// back to its defaults): the pull request lists the file's problems.</summary>
    [Fact]
    public void The_pull_request_lists_the_configuration_problems()
    {
        var problem = CodeCoverage.Dependencies.DependenciesYml.Parse("dependencies:\n  platforms:\n    dotnet:\n      companions: [{ match: x, versions: { \"11\": \"5.x\" } }]\n").Errors.Single();
        var run = new CodeCoverage.Entities.DependencyUpdateRun { ConfigProblems = [problem] };

        var body = CodeCoverage.Dependencies.Lifecycle.DependencyPullRequestBody.Render(run, new CodeCoverage.Dependencies.Lifecycle.DependencyRunReport(), null);

        body.Should().Contain("## Configuration problems").And.Contain("'11' is not a channel");
        CodeCoverage.Dependencies.Lifecycle.DependencyPullRequestBody.Render(new CodeCoverage.Entities.DependencyUpdateRun(), new CodeCoverage.Dependencies.Lifecycle.DependencyRunReport(), null)
            .Should().NotContain("Configuration problems");
    }

    [Fact]
    public void A_companion_with_no_release_in_the_new_range_never_holds_the_move()
    {
        var msbuild = Nuget("Microsoft.Build", "18.0.2", Candidate(EUpdateType.Minor, "18.14.0"));

        var outcome = Run(Dotnet("10.0", "10.0.100", "net10.0", Row("10.0", "10.0.5", "10.0.300")), msbuild, null,
            new SdkComponentVersions("10.0.1xx", "18.0.2", "5.0.0", "7.0.0"), new SdkComponentVersions("10.0.3xx", "18.6.3", "5.6.0", "7.6.0"));

        Planned(outcome).Should().Equal("platform:dotnet 10.0: ");
        outcome.Held.Concat(outcome.Available).Should().NotContain(e => e.Reason == EDependencyReportReason.Pending);
    }
}
