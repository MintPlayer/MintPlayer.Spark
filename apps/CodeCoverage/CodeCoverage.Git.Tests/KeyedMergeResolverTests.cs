using CodeCoverage.Git.MergeQueue;
using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.Git.MergeQueue.Resolution.Manifests;
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
    [InlineData("2.0.0-rc.9", "2.0.0-rc.10", "2.0.0-rc.10")]   // numeric prerelease identifiers
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

    [Theory]
    [InlineData("1.6.0", "1.8.0")]          // the base branch pinned lower (a bug in 1.7), the PR bumped
    [InlineData("1.8.0", "1.6.0")]          // the PR pinned lower
    [InlineData("1.7.0-rc.1", "1.8.0")]     // a prerelease of the base version is lower too
    public async Task A_version_lowered_on_either_side_is_refused_not_overridden_by_the_highest(string main, string pr)
    {
        // K1 (docs/coverage_backport_plan.md): a downgrade is deliberate, so "the highest" would undo it silently.
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Q", "1.7.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", main), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", pr), Package("Z", "1.0.0")));

        await RefusedAsync(bare, MsBuildRule, "lowered");
    }

    [Fact]
    public async Task A_dependency_range_lowered_on_one_side_is_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, PackageJson,
            Dependencies(("a", "^1.0.0"), ("q", "^1.2.0"), ("z", "^1.0.0")),
            Dependencies(("a", "^1.0.0"), ("q", "^1.1.0"), ("z", "^1.0.0")),
            Dependencies(("a", "^1.0.0"), ("q", "^1.4.0"), ("z", "^1.0.0")));

        await RefusedAsync(bare, PackageJsonRule, "lowered");
    }

    // --- composer.json (K2): require / require-dev keyed by package; the shape of laravel/laravel's own file ---

    private const string ComposerJson = "app/composer.json";

    private static readonly AutoResolveRule ComposerRule = AutoResolveRule.Create("**/composer.json", MergeQueueWireRules.KeyedMerge, [MergeQueueWireRules.JsonNoDuplicateKeys]);

    /// <summary>laravel/laravel's composer.json layout (4-space indent, a platform <c>php</c> first, two-part
    /// constraints), cut to the dependency objects and one object after them.</summary>
    private static string Composer(string php, string framework, string pint, params (string Name, string Range)[] extraDev)
        => "{\n    \"name\": \"laravel/laravel\",\n    \"type\": \"project\",\n    \"require\": {\n"
           + $"        \"php\": \"{php}\",\n        \"laravel/framework\": \"{framework}\",\n        \"laravel/tinker\": \"^3.0\"\n    }},\n"
           + "    \"require-dev\": {\n        \"fakerphp/faker\": \"^1.23\",\n"
           + string.Concat(extraDev.Select(d => $"        \"{d.Name}\": \"{d.Range}\",\n"))
           + $"        \"laravel/pint\": \"{pint}\",\n        \"phpunit/phpunit\": \"^12.5.12\"\n    }},\n"
           + "    \"autoload\": {\n        \"psr-4\": {\n            \"App\\\\\": \"app/\"\n        }\n    }\n}\n";

    [Fact]
    public async Task Composer_one_package_both_sides_raised_takes_the_highest_of_a_two_part_constraint()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, ComposerJson,
            Composer("^8.3", "^13.17", "^1.27"),
            Composer("^8.3", "^13.18", "^1.27"),
            Composer("^8.3", "^13.20", "^1.27"));

        (await ResolvedAsync(bare, ComposerRule, ComposerJson)).Should().Be(Composer("^8.3", "^13.20", "^1.27"));
    }

    [Fact]
    public async Task Composer_the_php_platform_requirement_both_sides_raised_is_refused()
    {
        // php, ext-* and lib-* are the platform, not packages: on a release line they are the line itself.
        using var repo = new TestRepo();
        var bare = Fixture(repo, ComposerJson,
            Composer("^8.3", "^13.17", "^1.27"),
            Composer("^8.4", "^13.17", "^1.27"),
            Composer("^8.5", "^13.17", "^1.27"));

        await RefusedAsync(bare, ComposerRule, "changed on both sides");
    }

    [Fact]
    public async Task Composer_dev_packages_added_on_both_sides_at_one_place_are_both_kept()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, ComposerJson,
            Composer("^8.3", "^13.17", "^1.27"),
            Composer("^8.3", "^13.17", "^1.27", ("laravel/pail", "^1.2.5")),
            Composer("^8.3", "^13.17", "^1.27", ("mockery/mockery", "^1.6")));

        (await ResolvedAsync(bare, ComposerRule, ComposerJson)).Should().Be(
            Composer("^8.3", "^13.17", "^1.27", ("laravel/pail", "^1.2.5"), ("mockery/mockery", "^1.6")));
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

    [Fact]
    public async Task One_package_bumped_on_one_side_and_removed_on_the_other_is_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Q", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", "1.1.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.1.0"), Package("Z", "1.0.0")));

        await RefusedAsync(bare, MsBuildRule, "removed on one side and changed on the other");
    }

    [Fact]
    public async Task A_side_that_holds_one_package_twice_is_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Q", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", "1.1.0"), Package("Q", "1.2.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", "1.3.0"), Package("Z", "1.0.0")));

        await RefusedAsync(bare, MsBuildRule, "one key twice");
    }

    [Fact]
    public async Task Comments_both_sides_wrote_before_one_package_are_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Q", "1.0.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), "<!-- main's reason -->", Package("Q", "1.1.0"), Package("Z", "1.0.0")),
            Items(Package("A", "1.0.0"), "<!-- the PR's reason -->", Package("Q", "1.2.0"), Package("Z", "1.0.0")));

        await RefusedAsync(bare, MsBuildRule, "comments before an entry changed on both sides");
    }

    [Fact]
    public async Task Two_conflict_regions_are_each_merged()
    {
        static string Spaced(string a, string z)
            => Items(Package("A", a), "<None Include=\"1.md\" />", "<None Include=\"2.md\" />", "<None Include=\"3.md\" />", "<None Include=\"4.md\" />", Package("Z", z));
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj, Spaced("1.0.0", "1.0.0"), Spaced("1.1.0", "1.2.0"), Spaced("1.3.0", "1.1.0"));

        (await ResolvedAsync(bare, MsBuildRule, Csproj)).Should().Be(Spaced("1.3.0", "1.2.0"));
    }

    [Fact]
    public async Task A_file_both_sides_added_has_no_base_to_merge_keys_against()
    {
        using var repo = new TestRepo();
        repo.Write("README.md", "base\n");
        repo.Commit("base");
        repo.InWork("branch", "pr");
        repo.Write(Csproj, Items(Package("Q", "1.1.0")));
        repo.Commit("A");
        repo.Checkout("pr");
        repo.Write(Csproj, Items(Package("Q", "1.2.0")));
        repo.Commit("B");

        await RefusedAsync(repo.Bare(), MsBuildRule, "add/add");
    }

    [Fact]
    public async Task A_side_with_a_line_that_looks_like_a_conflict_marker_is_refused()
    {
        using var repo = new TestRepo();
        var bare = Fixture(repo, Csproj,
            Items(Package("A", "1.0.0"), Package("Q", "1.0.0")),
            Items(Package("A", "1.0.0"), Package("Q", "1.1.0")) + "=======\n",
            Items(Package("A", "1.0.0"), Package("Q", "1.2.0")));

        await RefusedAsync(bare, MsBuildRule, "looks like a conflict marker");
    }

    [Fact]
    public void Two_formats_of_one_name_are_a_wiring_error()
    {
        var msbuild = ManifestFormats.BuiltIn.Single(f => f.Name == "msbuild");
        Action act = () => _ = new KeyedMergeResolver([msbuild, msbuild]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_format_that_encloses_lines_outside_the_common_text_is_a_bug_not_a_merge()
    {
        var format = new Enclosing(ManifestFormats.BuiltIn.Single(f => f.Name == "msbuild"), Above: 5);
        Diff3Piece[] pieces = [new Diff3Piece.Common(["<ItemGroup>\n"]), new Diff3Piece.Region(["a\n"], ["b\n"], ["c\n"]), new Diff3Piece.Common(["</ItemGroup>\n"])];
        Action act = () => KeyedMergeResolver.Enclose(format, pieces);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>A format that encloses a fixed number of lines, whatever the text says.</summary>
    private sealed class Enclosing(IManifestFormat inner, int Above) : IManifestFormat
    {
        public string Name => inner.Name;
        public bool Handles(string fileName) => inner.Handles(fileName);
        public (int Above, int Below) Enclose(IReadOnlyList<string> before, IReadOnlyList<string> after) => (Above, 0);
        public ManifestSection? Read(IReadOnlyList<string> lines) => inner.Read(lines);
        public int? CompareVersions(ManifestEntry entry, string valueName, string a, string b, IReadOnlyList<string> before) => inner.CompareVersions(entry, valueName, a, b, before);
        public IReadOnlyList<string>? Write(ManifestEntry entry, IReadOnlyDictionary<string, string?> values) => inner.Write(entry, values);
        public IReadOnlyList<string> Join(IReadOnlyList<ManifestEntry> merged, IReadOnlyList<string> trailing, ManifestSection ours, ManifestSection theirs) => inner.Join(merged, trailing, ours, theirs);
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
