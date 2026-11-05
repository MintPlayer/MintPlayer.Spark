using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.Git.Tests._Infrastructure;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// keyed-merge (#570, docs/coverage_backport_PRD.md §8.3): a three-way merge per key of the conflicted regions of an MSBuild
/// file or a package.json, on real repositories (real merge-tree and <c>git merge-file --diff3</c> runs). <c>main</c> is
/// the base branch, <c>pr</c> the pull request. Items are collections keyed by type and Include, properties are scalars
/// keyed by name; a key one side left alone takes the other side, equal values are kept once, a package version both
/// sides changed takes the highest, and anything else both sides changed stays a conflict.
/// </summary>
public class KeyedMergeResolverTests
{
    private const string Csproj = "libs/Lib/Lib.csproj";
    private const string PackageJson = "libs/ng/package.json";

    private static readonly AutoResolveRule MsBuildRule = AutoResolveRule.Create("**/*.csproj", MergeQueueWireRules.KeyedMerge, [MergeQueueWireRules.MsBuildNoDuplicateKeys]);
    private static readonly AutoResolveRule PackageJsonRule = AutoResolveRule.Create("**/package.json", MergeQueueWireRules.KeyedMerge, [MergeQueueWireRules.JsonNoDuplicateKeys]);

    private static string Fixture(TestRepo repo, string path, string @base, string main, string pr)
    {
        repo.Write(path, @base);
        repo.Commit("base");
        repo.InWork("branch", "pr");
        repo.Write(path, main);
        repo.Commit("A");
        repo.Checkout("pr");
        repo.Write(path, pr);
        repo.Commit("B");
        return repo.Bare();
    }

    private static Task<MergeOutcome> MergeAsync(string bare, AutoResolveRule rule)
        => new MergeQueueResolver(TestRepo.Runner()).MergeAsync(bare, "main", "pr", [rule], CancellationToken.None);

    private static string Blob(string bare, string tree, string path) => TestRepo.Git(bare, "cat-file", "blob", $"{tree}:{path}");

    private static string Items(params string[] lines) => Project($"  <ItemGroup>\n{string.Concat(lines.Select(l => $"    {l}\n"))}  </ItemGroup>\n");

    private static string Properties(params string[] lines) => Project($"  <PropertyGroup>\n{string.Concat(lines.Select(l => $"    {l}\n"))}  </PropertyGroup>\n");

    private static string Project(string body) => $"<Project Sdk=\"Microsoft.NET.Sdk\">\n{body}</Project>\n";

    private static string Package(string name, string version) => $"<PackageReference Include=\"{name}\" Version=\"{version}\" />";

    private static async Task<string> ResolvedAsync(string bare, AutoResolveRule rule, string path)
    {
        var resolved = (await MergeAsync(bare, rule)).Should().BeOfType<MergeOutcome.Resolved>().Which;
        resolved.Resolutions.Should().ContainSingle().Which.Strategy.Should().Be(MergeQueueWireRules.KeyedMerge);
        return Blob(bare, resolved.Tree, path);
    }

    private static async Task RefusedAsync(string bare, AutoResolveRule rule, string because)
    {
        var evicted = (await MergeAsync(bare, rule)).Should().BeOfType<MergeOutcome.Evicted>().Which;
        evicted.Reason.Should().Be(EvictionReason.ResolverRefused);
        evicted.Detail.Should().Contain(because);
    }

    // --- MSBuild items: collections keyed by type and Include ---

    [Fact]
    public async Task Edits_of_two_adjacent_packages_are_both_kept()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("B", "1.0.0"), Package("C", "1.0.0")),
            Items(Package("A", "1.1.0"), Package("B", "1.0.0"), Package("C", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("B", "1.1.0"), Package("C", "1.0.0")));

        (await ResolvedAsync(bare, MsBuildRule, Csproj)).Should().Be(Items(Package("A", "1.1.0"), Package("B", "1.1.0"), Package("C", "1.0.0")));
    }

    [Fact]
    public async Task Distinct_packages_inserted_at_one_place_are_kept_base_branch_first_and_a_shared_one_once()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Shared", "2.0.0"), Package("FromMain", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Shared", "2.0.0"), Package("FromPr", "1.0.0"), Package("Z", "1.0.0")));

        (await ResolvedAsync(bare, MsBuildRule, Csproj)).Should().Be(
            Items(Package("A", "1.0.0"), Package("Shared", "2.0.0"), Package("FromMain", "1.0.0"), Package("FromPr", "1.0.0"), Package("Z", "1.0.0")));
    }

    [Theory]
    [InlineData("1.9.0", "1.10.0", "1.10.0")]                  // numeric, not ordinal
    [InlineData("1.10.0", "1.9.0", "1.10.0")]                  // whichever side holds it
    [InlineData("11.0.0-rc.2", "11.0.0", "11.0.0")]            // a release outranks its prerelease
    [InlineData("1.0.0-rc.9", "1.0.0-rc.10", "1.0.0-rc.10")]   // numeric prerelease identifiers
    [InlineData("2.0.0-preview.1", "1.9.9", "2.0.0-preview.1")]
    public async Task One_package_both_sides_bumped_takes_the_highest_version(string main, string pr, string expected)
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Q", "1.7.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", main), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", pr), Package("Z", "1.0.0")));

        (await ResolvedAsync(bare, MsBuildRule, Csproj)).Should().Be(Items(Package("A", "1.0.0"), Package("Q", expected), Package("Z", "1.0.0")));
    }

    [Fact]
    public async Task One_package_both_sides_inserted_with_another_version_takes_the_highest()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("QRCoder", "1.8.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("qrcoder", "1.9.0"), Package("Z", "1.0.0")));

        // The key compares case-insensitively, as NuGet does; the base branch's spelling stays.
        (await ResolvedAsync(bare, MsBuildRule, Csproj)).Should().Be(Items(Package("A", "1.0.0"), Package("QRCoder", "1.9.0"), Package("Z", "1.0.0")));
    }

    [Fact]
    public async Task A_version_written_as_child_metadata_takes_the_highest_too()
    {
        static string Body(string version) => $"<PackageReference Include=\"Q\">\n      <Version>{version}</Version>\n      <PrivateAssets>all</PrivateAssets>\n    </PackageReference>";
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Body("1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Body("1.2.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Body("1.3.0"), Package("Z", "1.0.0")));

        (await ResolvedAsync(bare, MsBuildRule, Csproj)).Should().Be(Items(Package("A", "1.0.0"), Body("1.3.0"), Package("Z", "1.0.0")));
    }

    [Fact]
    public async Task Different_metadata_of_one_package_changed_on_each_side_are_both_applied()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Q", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", "1.1.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), "<PackageReference Include=\"Q\" Version=\"1.0.0\" PrivateAssets=\"all\" />", Package("Z", "1.0.0")));

        (await ResolvedAsync(bare, MsBuildRule, Csproj)).Should().Be(
            Items(Package("A", "1.0.0"), "<PackageReference Include=\"Q\" Version=\"1.1.0\" PrivateAssets=\"all\" />", Package("Z", "1.0.0")));
    }

    [Fact]
    public async Task A_comment_travels_with_the_entry_it_precedes()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), "<!-- why M -->", Package("M", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), "<!-- why P -->", Package("P", "1.0.0"), Package("Z", "1.0.0")));

        (await ResolvedAsync(bare, MsBuildRule, Csproj)).Should().Be(
            Items(Package("A", "1.0.0"), "<!-- why M -->", Package("M", "1.0.0"), "<!-- why P -->", Package("P", "1.0.0"), Package("Z", "1.0.0")));
    }

    [Fact]
    public async Task One_package_removed_on_one_side_and_bumped_on_the_other_is_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Q", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.1.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", "1.1.0"), Package("Z", "1.0.0")));

        await RefusedAsync(bare, MsBuildRule, "removed on one side and changed on the other");
    }

    [Theory]
    [InlineData("1.*", "1.2.0")]            // a floating version has no order
    [InlineData("[1.0,2.0)", "1.5.0")]      // nor has a range
    [InlineData("$(QVersion)", "1.5.0")]    // nor a property
    public async Task Versions_that_cannot_be_ordered_are_refused(string main, string pr)
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Q", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", main), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", pr), Package("Z", "1.0.0")));

        await RefusedAsync(bare, MsBuildRule, "changed on both sides");
    }

    [Fact]
    public async Task Another_attribute_both_sides_changed_is_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), "<None Include=\"x.md\" Pack=\"true\" PackagePath=\"\\\" />", Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), "<None Include=\"x.md\" Pack=\"true\" PackagePath=\"docs\" />", Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), "<None Include=\"x.md\" Pack=\"true\" PackagePath=\"content\" />", Package("Z", "1.0.0")));

        await RefusedAsync(bare, MsBuildRule, "changed on both sides");
    }

    // --- MSBuild properties: scalars keyed by name ---

    [Fact]
    public async Task Edits_of_two_adjacent_properties_are_both_kept()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Properties("<Nullable>enable</Nullable>", "<LangVersion>13</LangVersion>", "<IsPackable>true</IsPackable>"),
            Properties("<Nullable>disable</Nullable>", "<LangVersion>13</LangVersion>", "<IsPackable>true</IsPackable>"),
            Properties("<Nullable>enable</Nullable>", "<LangVersion>preview</LangVersion>", "<IsPackable>true</IsPackable>"));

        (await ResolvedAsync(bare, MsBuildRule, Csproj)).Should().Be(
            Properties("<Nullable>disable</Nullable>", "<LangVersion>preview</LangVersion>", "<IsPackable>true</IsPackable>"));
    }

    [Fact]
    public async Task One_property_both_sides_changed_is_refused_even_when_it_looks_like_a_version()
    {
        using var repo = new TestRepo();
        // A property is a scalar with no order; <Version> is the project's own version, not a dependency.
        var bare = Fixture(repo, Csproj,
            Properties("<Nullable>enable</Nullable>", "<Version>1.0.0</Version>", "<IsPackable>true</IsPackable>"),
            Properties("<Nullable>enable</Nullable>", "<Version>1.1.0</Version>", "<IsPackable>true</IsPackable>"),
            Properties("<Nullable>enable</Nullable>", "<Version>1.2.0</Version>", "<IsPackable>true</IsPackable>"));

        await RefusedAsync(bare, MsBuildRule, "changed on both sides");
    }

    [Fact]
    public async Task A_region_with_lines_that_are_not_keyed_entries_is_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Project("  <Target Name=\"T\">\n    <Message Text=\"a\" />\n  </Target>\n"),
            Project("  <Target Name=\"T\">\n    <Message Text=\"main\" />\n  </Target>\n"),
            Project("  <Target Name=\"T\">\n    <Message Text=\"pr\" />\n  </Target>\n"));

        await RefusedAsync(bare, MsBuildRule, "not a keyed entry");
    }

    // --- package.json: dependencies keyed by package name ---

    private static string Dependencies(params (string Name, string Range)[] deps)
        => "{\n  \"name\": \"@acme/lib\",\n  \"dependencies\": {\n"
           + string.Join(",\n", deps.Select(d => $"    \"{d.Name}\": \"{d.Range}\"")) + "\n  }\n}\n";

    [Fact]
    public async Task One_dependency_both_sides_bumped_takes_the_highest_range_of_one_operator()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, PackageJson,
            Dependencies(("a", "^1.0.0"), ("q", "^1.2.0"), ("z", "^1.0.0")),
            Dependencies(("a", "^1.0.0"), ("q", "^1.3.0"), ("z", "^1.0.0")),
            Dependencies(("a", "^1.0.0"), ("q", "^1.10.0"), ("z", "^1.0.0")));

        (await ResolvedAsync(bare, PackageJsonRule, PackageJson)).Should().Be(Dependencies(("a", "^1.0.0"), ("q", "^1.10.0"), ("z", "^1.0.0")));
    }

    [Fact]
    public async Task Ranges_of_different_operators_are_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, PackageJson,
            Dependencies(("a", "^1.0.0"), ("q", "^1.2.0"), ("z", "^1.0.0")),
            Dependencies(("a", "^1.0.0"), ("q", "~1.3.0"), ("z", "^1.0.0")),
            Dependencies(("a", "^1.0.0"), ("q", "^1.4.0"), ("z", "^1.0.0")));

        await RefusedAsync(bare, PackageJsonRule, "changed on both sides");
    }

    [Fact]
    public async Task The_package_own_version_both_sides_bumped_is_refused()
    {
        // The census of the MintPlayer repositories (PRD §8.1): the conflict head-to-head pairs met most is the package's own
        // "version", which is no dependency range and has no winner.
        static string Manifest(string version) => $"{{\n  \"name\": \"@acme/lib\",\n  \"version\": \"{version}\",\n  \"dependencies\": {{\n    \"a\": \"^1.0.0\"\n  }}\n}}\n";
        using var repo = new TestRepo();
        var bare = Fixture(repo, PackageJson, Manifest("15.0.3"), Manifest("15.0.4"), Manifest("15.0.5"));

        await RefusedAsync(bare, PackageJsonRule, "changed on both sides");
    }

    [Fact]
    public async Task Dependencies_appended_on_both_sides_are_kept_with_the_commas_of_a_valid_object()
    {
        using var repo = new TestRepo();
        // Both sides add a comma to the last entry and append their own: the base's last line conflicts.
        var bare = Fixture(repo, PackageJson,
            Dependencies(("a", "^1.0.0")),
            Dependencies(("a", "^1.0.0"), ("m", "^2.0.0")),
            Dependencies(("a", "^1.0.0"), ("p", "^3.0.0")));

        (await ResolvedAsync(bare, PackageJsonRule, PackageJson)).Should().Be(Dependencies(("a", "^1.0.0"), ("m", "^2.0.0"), ("p", "^3.0.0")));
    }
}
