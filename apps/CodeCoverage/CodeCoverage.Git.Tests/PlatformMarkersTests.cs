using CodeCoverage.Git.Backports;
using CodeCoverage.Git.Tests._Infrastructure;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// S2, marker normalization (#570, docs/coverage_backport_plan.md, PRD §5.3 step 1 and D8 as sharpened by S1): before a
/// backport's three-way merge, the platform values in the merge base and the change ("theirs") are rewritten to the
/// release line's own values, per occurrence, so a marker-only hunk vanishes and a real edit next to a marker merges.
/// A change the pull request made to a value of the line's own platform is left alone and reported. Every test merges
/// with the real <c>git merge-file</c>, so "merges clean" means what git says.
/// </summary>
public class PlatformMarkersTests
{
    private const string Csproj = "libs/Lib/Lib.csproj";

    /// <summary>The fixture of research note 2 (exp1): a project with its framework, nullable setting and two packages.</summary>
    private static string Project(string tfm, string nullable = "enable", string framework = "11.0.0", string qr = "1.6.0", string? extra = null, string newline = "\n")
        => string.Join(newline, (string[])[
            "<Project Sdk=\"Microsoft.NET.Sdk\">",
            "  <PropertyGroup>",
            $"    <TargetFramework>{tfm}</TargetFramework>",
            $"    <Nullable>{nullable}</Nullable>",
            "  </PropertyGroup>",
            "  <ItemGroup>",
            $"    <PackageReference Include=\"Microsoft.Extensions.Hosting\" Version=\"{framework}\" />",
            .. extra is null ? Array.Empty<string>() : [extra],
            $"    <PackageReference Include=\"QRCoder\" Version=\"{qr}\" />",
            "  </ItemGroup>",
            "</Project>",
            ""]);

    /// <summary>The release line's copy: .NET 10.</summary>
    private static readonly string Line = Project("net10.0", framework: "10.0.11");

    /// <summary>git merge-file over the three texts: the conflict count and the result.</summary>
    private static (int Conflicts, string Merged) Merge(string ours, string @base, string theirs)
    {
        using var repo = new TestRepo();
        var (o, b, t) = (Path.Combine(repo.Root, "ours"), Path.Combine(repo.Root, "base"), Path.Combine(repo.Root, "theirs"));
        File.WriteAllText(o, ours);
        File.WriteAllText(b, @base);
        File.WriteAllText(t, theirs);
        var (code, output, _) = TestRepo.TryGit(repo.Root, "merge-file", "-p", "--diff3", o, b, t);
        return (code, output);
    }

    private static (int Conflicts, string Merged, MarkerNormalization Normalization) NormalizedMerge(string path, string ours, string @base, string theirs)
    {
        var normalization = PlatformMarkers.Normalize(path, ours, @base, theirs)!;
        var (conflicts, merged) = Merge(ours, normalization.Base!, normalization.Theirs!);
        return (conflicts, merged, normalization);
    }

    // --- research note 2's scenarios ---

    [Fact]
    public void B_an_edit_next_to_the_framework_merges_clean_and_keeps_the_line_framework()
    {
        var @base = Project("net11.0");
        var theirs = Project("net11.0", nullable: "disable");

        Merge(Line, @base, theirs).Conflicts.Should().BeGreaterThan(0, "today: the edit sits next to the differing marker line");

        var (conflicts, merged, normalization) = NormalizedMerge(Csproj, Line, @base, theirs);
        conflicts.Should().Be(0);
        merged.Should().Be(Project("net10.0", nullable: "disable", framework: "10.0.11"));
        normalization.PlatformChanges.Should().BeEmpty();
    }

    [Fact]
    public void C_a_global_json_bump_above_the_line_leaves_nothing_to_apply()
    {
        static string GlobalJson(string sdk) => $"{{\n  \"sdk\": {{\n    \"version\": \"{sdk}\",\n    \"rollForward\": \"latestFeature\"\n  }}\n}}\n";

        Merge(GlobalJson("10.0.100"), GlobalJson("11.0.100"), GlobalJson("11.0.200")).Conflicts.Should().BeGreaterThan(0);

        var (conflicts, merged, normalization) = NormalizedMerge("global.json", GlobalJson("10.0.100"), GlobalJson("11.0.100"), GlobalJson("11.0.200"));
        conflicts.Should().Be(0);
        merged.Should().Be(GlobalJson("10.0.100"), "a marker-only change is empty on the line");
        normalization.PlatformChanges.Should().BeEmpty();
    }

    [Fact]
    public void A_pull_request_that_multi_targets_the_line_framework_still_conflicts_and_is_reported()
    {
        // D8's safety rule: a real edit of the line's own platform value never vanishes.
        var (conflicts, _, normalization) = NormalizedMerge(Csproj, Line, Project("net11.0"), Project("net10.0;net11.0"));

        conflicts.Should().BeGreaterThan(0);
        normalization.PlatformChanges.Should().Equal([new PlatformChange(Csproj, "TargetFramework", "net11.0", "net10.0;net11.0")]);
    }

    // --- per occurrence (S1's sharpened rule) ---

    [Fact]
    public void A_framework_package_bump_above_the_line_vanishes_and_a_package_bump_beside_it_applies()
    {
        var (conflicts, merged, _) = NormalizedMerge(Csproj, Line, Project("net11.0", framework: "11.0.0"), Project("net11.0", framework: "11.0.1", qr: "1.7.0"));

        conflicts.Should().Be(0);
        merged.Should().Be(Project("net10.0", framework: "10.0.11", qr: "1.7.0"));
    }

    [Fact]
    public void A_framework_package_the_pull_request_adds_takes_the_line_version_of_its_sibling()
    {
        // #461 added framework packages at 11.x next to an existing one; on the line they are 10.x.
        const string Added = "    <PackageReference Include=\"Microsoft.Extensions.Caching.Memory\" Version=\"{0}\" />";

        var (conflicts, merged, normalization) = NormalizedMerge(Csproj, Line, Project("net11.0"), Project("net11.0", extra: string.Format(Added, "11.0.0")));

        conflicts.Should().Be(0);
        merged.Should().Be(Project("net10.0", framework: "10.0.11", extra: string.Format(Added, "10.0.11")));
        normalization.PlatformChanges.Should().BeEmpty();
    }

    [Fact]
    public void A_change_of_a_value_on_the_line_own_major_is_left_to_git_and_reported()
    {
        static string GlobalJson(string sdk) => $"{{\n  \"sdk\": {{\n    \"version\": \"{sdk}\"\n  }}\n}}\n";

        // master was still on .NET 10 here: the patch bump is the line's business, so it applies.
        var (conflicts, merged, normalization) = NormalizedMerge("global.json", GlobalJson("10.0.100"), GlobalJson("10.0.100"), GlobalJson("10.0.200"));

        conflicts.Should().Be(0);
        merged.Should().Be(GlobalJson("10.0.200"));
        normalization.PlatformChanges.Should().Equal([new PlatformChange("global.json", "global.json sdk", "10.0.100", "10.0.200")]);
    }

    [Fact]
    public void A_workflow_step_added_next_to_the_sdk_setup_merges_and_keeps_the_line_sdk()
    {
        static string Workflow(string sdk, string? step = null)
            => "jobs:\n  build:\n    steps:\n      - uses: actions/setup-dotnet@v5\n        with:\n"
               + $"          dotnet-version: '{sdk}'\n" + (step ?? "") + "      - run: dotnet build\n";
        const string Step = "      - run: dotnet tool restore\n";

        var (conflicts, merged, _) = NormalizedMerge(".github/workflows/build.yml", Workflow("10.0.x"), Workflow("11.0.x"), Workflow("11.0.x", Step));

        conflicts.Should().Be(0);
        merged.Should().Be(Workflow("10.0.x", Step));
    }

    [Fact]
    public void An_angular_range_bump_vanishes_and_a_dependency_added_beside_it_applies()
    {
        static string Package(string core, string? extra = null)
            => "{\n  \"name\": \"@acme/app\",\n  \"dependencies\": {\n    \"@angular/common\": \"" + core + "\",\n    \"@angular/core\": \"" + core + "\",\n"
               + (extra ?? "") + "    \"rxjs\": \"~7.8.0\"\n  }\n}\n";
        const string Added = "    \"date-fns\": \"^4.1.0\",\n";

        var (conflicts, merged, _) = NormalizedMerge("apps/web/package.json", Package("^21.2.0"), Package("^22.1.0"), Package("^22.2.0", Added));

        conflicts.Should().Be(0);
        merged.Should().Be(Package("^21.2.0", Added));
    }

    // --- coverage.yml backport.markers (D8: extendable and disableable) ---

    [Fact]
    public void A_disabled_kind_is_left_to_the_plain_merge()
    {
        var options = new PlatformMarkerOptions(["tfm"], []);
        var @base = Project("net11.0");

        var normalization = PlatformMarkers.Normalize(Csproj, Line, @base, @base, options)!;

        normalization.Base.Should().Be(Project("net11.0", framework: "10.0.11"), "the framework package is still normalized, the TFM not");
    }

    [Fact]
    public void A_configured_package_is_a_marker_in_msbuild_and_npm_alike()
    {
        // A first-party platform-anchored package (versioning D7), such as @mintplayer/ng-bootstrap.
        var options = new PlatformMarkerOptions([], ["@mintplayer/ng-bootstrap", "MintPlayer.Spark*"]);
        static string Package(string ng, string extra = "") => "{\n  \"dependencies\": {\n    \"@mintplayer/ng-bootstrap\": \"" + ng + "\",\n" + extra + "    \"rxjs\": \"~7.8.0\"\n  }\n}\n";
        const string Added = "    \"date-fns\": \"^4.1.0\",\n";

        var (conflicts, merged) = Merge(Package("^21.4.0"),
            PlatformMarkers.Normalize("package.json", Package("^21.4.0"), Package("^22.34.0"), Package("^22.36.0", Added), options)!.Base!,
            PlatformMarkers.Normalize("package.json", Package("^21.4.0"), Package("^22.34.0"), Package("^22.36.0", Added), options)!.Theirs!);
        conflicts.Should().Be(0);
        merged.Should().Be(Package("^21.4.0", Added));

        const string Spark = "    <PackageReference Include=\"MintPlayer.Spark.Abstractions\" Version=\"{0}\" />";
        var normalization = PlatformMarkers.Normalize(Csproj, Project("net10.0", framework: "10.0.11", extra: string.Format(Spark, "10.0.0-preview.90")),
            Project("net11.0", extra: string.Format(Spark, "11.0.0-preview.3")), Project("net11.0", extra: string.Format(Spark, "11.0.0-preview.3")), options)!;
        normalization.Theirs.Should().Contain(string.Format(Spark, "10.0.0-preview.90"));
        PlatformMarkers.Normalize(Csproj, Line, Line, Line)!.Theirs.Should().Be(Line, "without the option nothing else is a marker");
    }

    [Theory]
    [InlineData("tfm", true)]
    [InlineData("framework-packages", true)]
    [InlineData("global.json", true)]
    [InlineData("setup-action", true)]
    [InlineData("docker", false)]
    public void The_kinds_a_repository_can_disable_are_named(string kind, bool known)
        => PlatformMarkerOptions.KindNames.Contains(kind).Should().Be(known);

    // --- fidelity and scope ---

    [Fact]
    public void Only_the_value_is_rewritten_so_line_endings_and_spacing_are_kept()
    {
        var @base = Project("net11.0", newline: "\r\n");

        var normalization = PlatformMarkers.Normalize(Csproj, Project("net10.0", framework: "10.0.11", newline: "\r\n"), @base, @base)!;

        normalization.Base.Should().Be(Project("net10.0", framework: "10.0.11", newline: "\r\n"));
        normalization.Theirs.Should().Be(normalization.Base);
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("README.md")]
    [InlineData("package-lock.json")]   // a lockfile is regenerated, never normalized
    public void A_file_without_markers_is_not_normalized(string path)
    {
        PlatformMarkers.Normalize(path, "a\n", "b\n", "c\n").Should().BeNull();
    }

    [Fact]
    public void A_file_the_line_does_not_have_is_left_as_it_is()
    {
        // No line value to rewrite to; M5 decides what a new project on a line gets (PRD §8.3).
        var normalization = PlatformMarkers.Normalize(Csproj, null, null, Project("net11.0"))!;

        normalization.Base.Should().BeNull();
        normalization.Theirs.Should().Be(Project("net11.0"));
    }
}
