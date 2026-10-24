using System.IO.Compression;
using CodeCoverage.Versioning.Api;
using CodeCoverage.Versioning.Build;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>
/// The prerelease label (D8 as amended after master run 40124565898): the <c>versioning.prerelease</c> pin, the SDK a
/// .NET package's platform is built with, and the registry hash of a generator package.
/// </summary>
public class ReleaseLabelTests
{
    private const string Hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class FakeDiff(params string[] changed) : IInputDiff
    {
        public Task<IReadOnlyList<string>> ChangedAsync(string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(changed.Where(f => paths.Any(p => RepoPath.IsUnder(f, p))).ToList());
    }

    private static TaggedVersion Tag(string id, string version)
        => new(VersionTag.For(id, PackageVersion.Parse(version)), "c1", new TagAnnotation(Hash, null));

    /// <summary>One package, src/Core, depending on a beta the way Spark.Webhooks.GitHub depends on Octokit.GraphQL 0.4.0-beta.</summary>
    private static async Task<ReleasePlan> Plan(string tfm, IEnumerable<TaggedVersion> tags, VersioningSettings? settings = null, string? sdk = null,
        string resolvedBeta = "0.4.0-beta")
    {
        var tree = new InMemoryTree()
            .Add("src/Core/Core.csproj", $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Octokit.GraphQL" Version="{resolvedBeta}" /></ItemGroup></Project>""")
            .Add("src/Core/Guard.cs", "");
        var graph = await ProjectGraphBuilder.BuildAsync(tree);
        return await ReleasePlanner.PlanAsync(new ReleaseContext
        {
            Graph = graph,
            Head = "head",
            Tags = new TagHistory(tags),
            Diff = new FakeDiff("src/Core/Guard.cs"),
            Settings = settings ?? new VersioningSettings(),
            Build = new Dictionary<string, PackageBuildFacts>
            {
                ["Core"] = new() { ApiHash = Hash, ResolvedVersions = new Dictionary<string, string> { ["Octokit.GraphQL"] = resolvedBeta } },
            },
            Sdk = sdk,
        });
    }

    private static VersioningSettings Settings(string yamlPrerelease, Dictionary<string, VersioningPackageConfig?>? packages = null)
    {
        var config = new VersioningConfig { Prerelease = yamlPrerelease, Packages = packages };
        config.Validate().Should().BeEmpty();
        return config.ToSettings();
    }

    [Fact]
    public async Task Without_a_pin_a_beta_dependency_refuses_a_preview_line()
    {
        // Master run 40124565898: "11.0.0-beta.1 is not above 11.0.0-preview.105".
        var plan = await Plan("net11.0", [Tag("Core", "11.0.0-preview.105")]);

        plan.Refused.Should().ContainSingle().Which.Reason.Should().Contain("11.0.0-beta.1 is not above 11.0.0-preview.105");
    }

    [Fact]
    public async Task The_repository_pin_wins_over_a_less_mature_dependency()
    {
        var plan = await Plan("net11.0", [Tag("Core", "11.0.0-preview.105")], Settings("preview"));

        plan.Of("Core")!.Version.ToString().Should().Be("11.0.0-preview.106");
    }

    [Fact]
    public async Task The_repository_pin_labels_a_package_with_only_stable_dependencies()
    {
        var plan = await Plan("net11.0", [Tag("Core", "11.0.0-preview.86")], Settings("preview"), resolvedBeta: "1.0.0");

        plan.Of("Core")!.Version.ToString().Should().Be("11.0.0-preview.87");
    }

    [Fact]
    public async Task A_package_set_to_upstream_is_exempt_from_the_repository_pin()
    {
        var settings = Settings("preview", new() { ["Core"] = new VersioningPackageConfig { Prerelease = "upstream" } });

        var plan = await Plan("netstandard2.0", [Tag("Core", "1.0.0")], settings, resolvedBeta: "1.0.0");

        plan.Of("Core")!.Version.ToString().Should().Be("1.0.1");
    }

    [Fact]
    public async Task A_package_pin_wins_over_the_repository_pin()
    {
        var settings = Settings("preview", new() { ["Core"] = new VersioningPackageConfig { Prerelease = "rc" } });

        var plan = await Plan("net11.0", [], settings);

        plan.Of("Core")!.Version.ToString().Should().Be("11.0.0-rc.1");
    }

    [Fact]
    public async Task A_prerelease_sdk_of_the_targeted_major_labels_the_release()
    {
        var plan = await Plan("net11.0", [], sdk: "11.0.100-rc.2.26504.105", resolvedBeta: "1.0.0");

        plan.Of("Core")!.Version.ToString().Should().Be("11.0.0-rc.1");
    }

    [Theory]
    [InlineData("12.0.100-preview.1.26101.1")] // a newer SDK building an older target says nothing about net11.0
    [InlineData("11.0.100")]
    public async Task Only_a_prerelease_sdk_of_the_same_major_counts(string sdk)
    {
        var plan = await Plan("net11.0", [], sdk: sdk, resolvedBeta: "1.0.0");

        plan.Of("Core")!.Version.ToString().Should().Be("11.0.0");
    }

    [Fact]
    public async Task A_free_package_ignores_the_sdk()
    {
        var plan = await Plan("netstandard2.0", [Tag("Core", "3.0.0")], sdk: "11.0.100-rc.2.26504.105", resolvedBeta: "1.0.0");

        plan.Of("Core")!.Version.ToString().Should().Be("3.0.1");
    }

    [Theory]
    [InlineData("gamma")]
    [InlineData("Preview")]
    public void An_unknown_label_is_refused(string label)
    {
        new VersioningConfig { Prerelease = label }.Validate().Should().ContainSingle().Which.Should().Contain("versioning.prerelease");
        new VersioningConfig { Packages = new() { ["Core"] = new VersioningPackageConfig { Prerelease = label } } }.Validate()
            .Should().ContainSingle().Which.Should().Contain("versioning.packages.Core.prerelease");
    }

    [Fact]
    public void A_malformed_sdk_in_the_manifest_is_refused()
    {
        var manifest = new ReleaseManifest { Commit = new string('a', 40), Packages = [], Sdk = "not a version" };

        manifest.Validate().Should().ContainSingle().Which.Should().Be("sdk must be a version.");
    }

    [Fact]
    public void A_generator_package_is_hashed_from_its_analyzer_assembly_as_the_build_hashes_it()
    {
        // The generators ship under analyzers/dotnet/cs (IncludeBuildOutput=false), so a lib-only read found nothing and
        // every generator's API "counted as changed" on master run 40124565898.
        var assemblyPath = typeof(VersioningConfig).Assembly.Location;
        var package = new ProjectNode
        {
            Path = "src/Gen/Gen.csproj",
            Kind = PackageKind.NuGet,
            Id = "CodeCoverage.Versioning",
            Publishable = true,
            TargetFrameworks = ["netstandard2.0"],
        };
        using var nupkg = new MemoryStream();
        using (var zip = new ZipArchive(nupkg, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("analyzers/dotnet/cs/CodeCoverage.Versioning.dll").Open();
            using var source = File.OpenRead(assemblyPath);
            source.CopyTo(entry);
        }
        nupkg.Position = 0;

        using var built = File.OpenRead(assemblyPath);
        var expected = DotnetApiListing.Hash(DotnetApiListing.List(built).Select(l => $"netstandard2.0 {l}").ToList());
        PublicPackageRegistry.NuGetHash(nupkg, package).Should().Be(expected);
    }
}
