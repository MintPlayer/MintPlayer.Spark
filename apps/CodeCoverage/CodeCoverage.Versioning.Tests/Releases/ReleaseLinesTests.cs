using CodeCoverage.Versioning.Git;
using CodeCoverage.Versioning.Graph;
using CodeCoverage.Versioning.Releases;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.Versioning.Tests.Releases;

/// <summary>
/// #568 S3: release lines scoped to an ecosystem (D1, D2), releasing only from lines (D15), and unanchored packages on
/// the newest line of their ecosystem (D19). One repository mixes net10/net11, netstandard and Angular packages.
/// </summary>
public class ReleaseLinesTests
{
    private const string Hash = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private sealed class FakeDiff(params string[] changed) : IInputDiff
    {
        public Task<IReadOnlyList<string>> ChangedAsync(string from, string to, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(changed.Where(f => paths.Any(p => RepoPath.IsUnder(f, p))).ToList());
    }

    private static TaggedVersion Tag(string id, string version)
        => new(VersionTag.For(id, PackageVersion.Parse(version)), "c1", new TagAnnotation(Hash, null));

    private static string Library(string tfm)
        => $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup></Project>";

    /// <summary>Core targets <paramref name="coreTfm"/>, Text is netstandard, ng-acme peers on Angular <paramref name="angular"/>, util is a plain node library.</summary>
    private static InMemoryTree Mixed(string coreTfm = "net10.0", string angular = "21.2.0") => new InMemoryTree()
        .Add("src/Core/Core.csproj", Library(coreTfm))
        .Add("src/Core/Guard.cs", "")
        .Add("src/Text/Text.csproj", Library("netstandard2.0"))
        .Add("src/Text/Text.cs", "")
        .Add("package.json", """{ "name": "root", "private": true, "workspaces": ["packages/*"] }""")
        .Add("packages/util/package.json", """{ "name": "@acme/util", "version": "0.0.0-PLACEHOLDER" }""")
        .Add("packages/util/index.js", "")
        .Add("libs/ng-acme/project.json", "{}")
        .Add("libs/ng-acme/ng-package.json", """{ "dest": "../../dist/libs/ng-acme" }""")
        .Add("libs/ng-acme/package.json", """{ "name": "@acme/ng-acme", "peerDependencies": { "@angular/core": ">=21.0.0" } }""")
        .Add("libs/ng-acme/index.ts", "")
        .Add("package-lock.json", $$"""{ "lockfileVersion": 3, "packages": { "node_modules/@angular/core": { "version": "{{angular}}" } } }""");

    private static readonly string[] Everything = ["src/Core/Guard.cs", "src/Text/Text.cs", "libs/ng-acme/index.ts", "packages/util/index.js"];

    private static readonly TaggedVersion[] Tags =
    [
        Tag("Core", "10.1.0"), Tag("Core", "11.0.0"), Tag("Text", "3.0.0"), Tag("@acme/ng-acme", "21.0.0"), Tag("@acme/util", "1.0.0"),
    ];

    private static async Task<ReleasePlan> PlanAsync(InMemoryTree tree, string? branch, TaggedVersion[]? tags = null, VersioningSettings? settings = null)
    {
        settings ??= new VersioningSettings { Lines = ReleaseLinePattern.Default };
        var graph = await ProjectGraphBuilder.BuildAsync(tree);
        ReleaseLine? line = null;
        settings.Lines?.TryMatch(branch, out line);
        return await ReleasePlanner.PlanAsync(new ReleaseContext
        {
            Graph = graph,
            Head = "head",
            Tags = new TagHistory(tags ?? Tags),
            Diff = new FakeDiff(Everything),
            Settings = settings,
            Build = graph.Packages.ToDictionary(p => p.Id!, _ => new PackageBuildFacts { ApiHash = Hash }),
            OnDefaultBranch = branch == "main",
            Line = line,
        });
    }

    [Theory]
    [InlineData("release/{ecosystem}-{major}.x", "release/dotnet-10.x", "dotnet", 10)]
    [InlineData("release/{ecosystem}-{major}.x", "release/angular-21.x", "angular", 21)]
    [InlineData("support/{major}/{ecosystem}", "support/8/dotnet", "dotnet", 8)]
    public void A_pattern_matches_its_lines(string pattern, string branch, string ecosystem, int major)
    {
        var parsed = ReleaseLinePattern.Parse(pattern);

        parsed.TryMatch(branch, out var line).Should().BeTrue();
        line.Should().Be(new ReleaseLine(ecosystem, major));
        parsed.Format(line!).Should().Be(branch);
    }

    [Theory]
    [InlineData("release/10.x")]
    [InlineData("release/dotnet-10")]
    [InlineData("release/Dotnet-10.x")]
    [InlineData("release/dotnet-010.x")]
    [InlineData("release/-10.x")]
    [InlineData("main")]
    public void Other_branches_are_no_line(string branch)
        => ReleaseLinePattern.Default.TryMatch(branch, out _).Should().BeFalse();

    [Theory]
    [InlineData("release/{major}.x")]               // D1: no plain release/N.x
    [InlineData("release/{ecosystem}.x")]
    [InlineData("release/{ecosystem}-{major}-{major}")]
    [InlineData("release/{ecosystem}-*.x")]
    [InlineData("release/{ecosystem} {major}")]
    [InlineData("/release/{ecosystem}-{major}")]
    public void A_pattern_without_both_placeholders_once_is_refused(string pattern)
        => new VersioningConfig { Lines = new VersioningLinesConfig { Pattern = pattern } }.Validate().Should().ContainSingle(e => e.Contains("versioning.lines.pattern"));

    [Fact]
    public void Lines_are_off_unless_the_section_is_there()
    {
        new VersioningConfig().ToSettings().Lines.Should().BeNull();
        new VersioningConfig { Lines = new VersioningLinesConfig() }.ToSettings().Lines!.Text.Should().Be(ReleaseLinePattern.DefaultText);
    }

    [Fact]
    public void A_line_pin_must_name_an_ecosystem()
        => new VersioningConfig { Packages = new() { ["Text"] = new VersioningPackageConfig { Line = "Dot Net" } } }
            .Validate().Should().ContainSingle(e => e.Contains("versioning.packages.Text.line"));

    [Fact]
    public async Task An_older_dotnet_line_releases_its_own_major_and_nothing_else()
    {
        // The red of S3: before lines, a branch release refused the whole run (ng-acme computes 21, Text is free).
        var plan = await PlanAsync(Mixed(), "release/dotnet-10.x");

        plan.Refused.Should().BeEmpty();
        plan.Releases.Select(r => r.Tag.Name).Should().Equal("Core/v10.1.1");
        plan.Skipped.Single(n => n.Id == "Text").Reason.Should().Contain("release/dotnet-11.x");
        plan.Skipped.Single(n => n.Id == "@acme/ng-acme").Reason.Should().Contain("release/angular-21.x");
    }

    [Fact]
    public async Task The_newest_dotnet_line_also_releases_the_unanchored_dotnet_packages()
    {
        var plan = await PlanAsync(Mixed("net11.0"), "release/dotnet-11.x");

        plan.Refused.Should().BeEmpty();
        plan.Releases.Select(r => r.Tag.Name).Should().BeEquivalentTo(["Core/v11.0.1", "Text/v3.0.1"]);
    }

    [Fact]
    public async Task An_angular_line_releases_the_angular_packages_and_the_plain_node_ones_beside_them()
    {
        // util has no framework; the repository's npm packages all ship for Angular, so it follows the Angular lines.
        var plan = await PlanAsync(Mixed(), "release/angular-21.x");

        plan.Refused.Should().BeEmpty();
        plan.Releases.Select(r => r.Tag.Name).Should().BeEquivalentTo(["@acme/ng-acme/v21.0.1", "@acme/util/v1.0.1"]);
    }

    [Fact]
    public async Task A_line_pin_moves_an_unanchored_package_to_another_ecosystem()
    {
        var settings = new VersioningSettings { Lines = ReleaseLinePattern.Default, LinePins = new Dictionary<string, string> { ["@acme/util"] = "dotnet" } };

        var angular = await PlanAsync(Mixed("net11.0"), "release/angular-21.x", settings: settings);
        var dotnet = await PlanAsync(Mixed("net11.0"), "release/dotnet-11.x", settings: settings);

        angular.Releases.Select(r => r.Id).Should().NotContain("@acme/util");
        dotnet.Releases.Select(r => r.Tag.Name).Should().Contain("@acme/util/v1.0.1");
    }

    [Fact]
    public async Task The_default_branch_releases_nothing_when_lines_are_on()
    {
        var plan = await PlanAsync(Mixed("net11.0"), "main");

        plan.Releases.Should().BeEmpty();
        plan.Refused.Should().BeEmpty();
        plan.Skipped.Should().HaveCount(4).And.OnlyContain(n => n.Reason.Contains("only from release lines"));
    }

    [Fact]
    public async Task A_backport_that_moves_a_package_off_its_line_is_refused_not_skipped()
    {
        // Core has 10.x tags, so it belongs on release/dotnet-10.x; a net11 TFM there is a mistake to stop, not to hide.
        var plan = await PlanAsync(Mixed("net11.0"), "release/dotnet-10.x");

        plan.Refused.Single().Id.Should().Be("Core");
    }

    [Fact]
    public async Task A_dependency_in_preview_on_the_newer_line_does_not_make_the_older_line_a_prerelease()
    {
        // Mapper references Core; Core's newest tag is 11.0.0-preview.3 on the dotnet-11 line, its 10.x line is stable.
        var tree = Mixed()
            .Add("src/Mapper/Mapper.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Core/Core.csproj\" /></ItemGroup></Project>")
            .Add("src/Mapper/Map.cs", "");
        var settings = new VersioningSettings { Lines = ReleaseLinePattern.Default };
        var graph = await ProjectGraphBuilder.BuildAsync(tree);

        var plan = await ReleasePlanner.PlanAsync(new ReleaseContext
        {
            Graph = graph,
            Head = "head",
            Tags = new TagHistory([Tag("Core", "10.1.0"), Tag("Core", "11.0.0-preview.3"), Tag("Mapper", "10.0.0")]),
            Diff = new FakeDiff("src/Mapper/Map.cs"),
            Settings = settings,
            Build = graph.Packages.ToDictionary(p => p.Id!, _ => new PackageBuildFacts { ApiHash = Hash }),
            OnDefaultBranch = false,
            Line = new ReleaseLine("dotnet", 10),
        });

        plan.Releases.Select(r => r.Tag.Name).Should().Equal("Mapper/v10.0.1");
    }

    [Fact]
    public async Task A_line_goes_stable_on_its_own_while_the_global_label_stays()
    {
        // #568 D14: versioning.prerelease pins preview; the dotnet-10 line overrides it to upstream, which is stable here.
        var settings = new VersioningConfig
        {
            Prerelease = "preview",
            Lines = new VersioningLinesConfig { Prerelease = new() { ["dotnet-10.x"] = "upstream" } },
        }.ToSettings();

        var ten = await PlanAsync(Mixed(), "release/dotnet-10.x", settings: settings);
        var eleven = await PlanAsync(Mixed("net11.0"), "release/dotnet-11.x", settings: settings);

        ten.Releases.Single(r => r.Id == "Core").Version.IsPrerelease.Should().BeFalse();
        eleven.Releases.Single(r => r.Id == "Core").Version.IsPrerelease.Should().BeTrue();
    }

    [Fact]
    public async Task A_package_pin_wins_over_its_lines_label()
    {
        var settings = new VersioningConfig
        {
            Lines = new VersioningLinesConfig { Prerelease = new() { ["dotnet-10.x"] = "rc" } },
            Packages = new() { ["Core"] = new VersioningPackageConfig { Prerelease = "beta" } },
        }.ToSettings();

        var plan = await PlanAsync(Mixed(), "release/dotnet-10.x", settings: settings);

        plan.Releases.Single(r => r.Id == "Core").Version.Label.Should().Be(PrereleaseLabel.Beta);
    }

    [Theory]
    [InlineData("release/dotnet-10.x", "upstream", "is not a line")]
    [InlineData("dotnet-010.x", "upstream", "is not a line")]
    [InlineData("Dotnet-10.x", "upstream", "is not a line")]
    [InlineData("dotnet-10.x", "gamma", "versioning.lines.prerelease.dotnet-10.x")]
    public void A_line_label_must_name_a_line_and_a_label(string key, string label, string error)
        => new VersioningConfig { Lines = new VersioningLinesConfig { Prerelease = new() { [key] = label } } }
            .Validate().Should().ContainSingle(e => e.Contains(error));

    [Fact]
    public async Task A_package_new_on_an_older_major_starts_its_line()
    {
        var plan = await PlanAsync(Mixed(), "release/dotnet-10.x", [Tag("Core", "11.0.0")]);

        plan.Releases.Select(r => r.Tag.Name).Should().Contain("Core/v10.0.0");
    }
}
