using CodeCoverage.Git.MergeQueue.Resolution;
using CodeCoverage.Git.MergeQueue.Resolution.Manifests;
using Microsoft.Extensions.DependencyInjection;

namespace CodeCoverage.Git.Tests;

/// <summary>
/// The manifest formats keyed-merge reads (#570 K2, docs/coverage_backport_plan.md): one <see cref="IManifestFormat"/>
/// service per ecosystem, injected as a list, each parsing its files into the one structure the merge works on
/// (<see cref="ManifestEntry"/>: a key, scalar or collection, values by name, and the entry's own text). A format knows
/// which files it reads, which values are versions and their order, and how to write values back in place; the
/// three-way merge itself is the resolver's (KeyedMergeResolverTests, end to end).
/// </summary>
public class ManifestFormatTests
{
    private static IManifestFormat Format(string name) => ManifestFormats.BuiltIn.Single(f => f.Name == name);

    /// <summary>Lines as diff3 hands them over: each with its terminator.</summary>
    private static string[] Lines(params string[] lines) => [.. lines.Select(l => l + "\n")];

    private static ManifestEntry Entry(string format, params string[] lines) => Format(format).Read(Lines(lines))!.Entries.Single();

    // --- the set ---

    [Fact]
    public void The_registered_formats_are_the_built_in_set_with_unique_names()
    {
        var registered = new ServiceCollection().AddMergeQueueResolution().BuildServiceProvider().GetServices<IManifestFormat>().Select(f => f.Name).ToList();

        registered.Should().OnlyHaveUniqueItems();
        registered.Order(StringComparer.Ordinal).Should().Equal(ManifestFormats.BuiltIn.Select(f => f.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("libs/Lib/Lib.csproj", "msbuild")]
    [InlineData("Directory.Packages.props", "msbuild")]
    [InlineData("Directory.Build.props", "msbuild")]
    [InlineData("build/Pack.targets", "msbuild")]
    [InlineData("apps/web/package.json", "npm")]
    [InlineData("composer.json", "composer")]
    // Lockfiles are take-base + regenerate (the presets), never keyed-merge: the regenerate run rebuilds them from the
    // merged manifest.
    [InlineData("package-lock.json", null)]
    [InlineData("packages.lock.json", null)]
    [InlineData("composer.lock", null)]
    [InlineData("tsconfig.json", null)]
    [InlineData("Directory.Packages.props.bak", null)]
    public void A_file_is_read_by_at_most_one_format(string path, string? expected)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];

        ManifestFormats.BuiltIn.Where(f => f.Handles(name)).Select(f => f.Name).Should().Equal(expected is null ? [] : [expected]);
    }

    // --- msbuild: items are collections, properties scalars ---

    [Fact]
    public void MsBuild_reads_an_item_as_a_collection_entry_with_its_comment_and_metadata_by_name()
    {
        var section = Format("msbuild").Read(Lines("    <!-- QR codes -->", "    <PackageReference Include=\"QRCoder\" Version=\"1.8.0\" PrivateAssets=\"all\" />"))!;

        var entry = section.Entries.Single();
        entry.Scalar.Should().BeFalse();
        entry.Kind.Should().Be("PackageReference");
        entry.Leading.Should().Equal(Lines("    <!-- QR codes -->"));
        entry.Values.Count.Should().Be(2);
        entry.Values["version"].Should().Be("1.8.0"); // metadata names compare case-insensitively, as MSBuild does
        entry.Values["PrivateAssets"].Should().Be("all");
    }

    [Fact]
    public void MsBuild_keys_an_item_by_type_include_and_condition_case_insensitively()
    {
        var key = Entry("msbuild", "<PackageReference Include=\"QRCoder\" Version=\"1.8.0\" />").Key;

        Entry("msbuild", "<PackageReference Include=\"qrcoder\" Version=\"1.9.0\" />").Key.Should().Be(key);
        Entry("msbuild", "<PackageVersion Include=\"QRCoder\" Version=\"1.8.0\" />").Key.Should().NotBe(key);
        Entry("msbuild", "<PackageReference Include=\"QRCoder\" Version=\"1.8.0\" Condition=\"'$(TargetFramework)' == 'net10.0'\" />").Key.Should().NotBe(key);
        Entry("msbuild", "<PackageReference Update=\"QRCoder\" Version=\"1.8.0\" />").Key.Should().NotBe(key);
    }

    [Fact]
    public void MsBuild_reads_child_metadata_into_the_same_values()
    {
        var entry = Entry("msbuild", "<PackageReference Include=\"Q\">", "  <Version>1.0.0</Version>", "  <PrivateAssets>all</PrivateAssets>", "</PackageReference>");

        entry.Lines.Count.Should().Be(4);
        entry.Values["Version"].Should().Be("1.0.0");
        entry.Values["PrivateAssets"].Should().Be("all");
    }

    [Fact]
    public void MsBuild_reads_a_property_as_a_scalar_keyed_by_name_and_condition()
    {
        var section = Format("msbuild").Read(Lines("<Version>1.0.0</Version>", "<Version Condition=\"'$(X)' == '1'\">2.0.0</Version>", "<Nullable />"))!;

        section.Entries.Should().HaveCount(3);
        section.Entries.Should().AllSatisfy(e => e.Scalar.Should().BeTrue());
        section.Entries.Select(e => e.Key).Should().OnlyHaveUniqueItems();
        section.Entries[0].Values[ManifestEntry.ScalarValue].Should().Be("1.0.0");
    }

    [Theory]
    [InlineData("<Target Name=\"T\">")]
    [InlineData("<Message Text=\"a\" />")]       // a task inside a target is no entry
    [InlineData("<ItemGroup>")]
    [InlineData("</ItemGroup>")]
    [InlineData("<!-- a comment that goes on")]
    public void MsBuild_refuses_a_line_that_is_not_an_entry(string line)
    {
        Format("msbuild").Read(Lines(line)).Should().BeNull();
    }

    [Fact]
    public void MsBuild_encloses_a_child_line_in_its_whole_item()
    {
        var format = Format("msbuild");

        format.Enclose(Lines("  <ItemGroup>", "    <PackageReference Include=\"Q\">"),
                Lines("      <PrivateAssets>all</PrivateAssets>", "    </PackageReference>", "  </ItemGroup>"))
            .Should().Be((1, 2));
        format.Enclose(Lines("  <ItemGroup>", "    <PackageReference Include=\"A\" Version=\"1.0.0\" />"),
                Lines("    <PackageReference Include=\"Z\" Version=\"1.0.0\" />", "  </ItemGroup>"))
            .Should().Be((0, 0));
    }

    [Theory]
    [InlineData("1.9.0", "1.10.0", -1)]               // numeric, not ordinal
    [InlineData("11.0.0-rc.2", "11.0.0", -1)]         // a release outranks its prerelease
    [InlineData("1.0.0-rc.9", "1.0.0-rc.10", -1)]     // numeric prerelease identifiers
    [InlineData("1.0.0-alpha", "1.0.0-1", 1)]         // numeric identifiers rank below alphanumeric ones
    [InlineData("2.0.0-preview.1", "1.9.9", 1)]
    [InlineData("1.2", "1.2.0", 0)]                   // NuGet pads to four parts
    [InlineData("1.2.0+build.5", "1.2.0", 0)]         // build metadata does not count
    public void MsBuild_orders_package_versions_as_NuGet_does(string a, string b, int expected)
    {
        var entry = Entry("msbuild", "<PackageReference Include=\"Q\" Version=\"1.0.0\" />");

        Math.Sign(Format("msbuild").CompareVersions(entry, "Version", a, b, []) ?? 99).Should().Be(expected);
    }

    [Theory]
    [InlineData("<Version>1.0.0</Version>", "")]                                          // a property is one value, no winner
    [InlineData("<PackageReference Include=\"Q\" Version=\"1.0.0\" PrivateAssets=\"all\" />", "PrivateAssets")]
    [InlineData("<Compile Include=\"a.cs\" Version=\"1.0.0\" />", "Version")]            // only package items carry versions
    public void MsBuild_gives_no_order_to_a_value_that_is_not_a_package_version(string line, string valueName)
    {
        Format("msbuild").CompareVersions(Entry("msbuild", line), valueName, "1.0.0", "2.0.0", []).HasValue.Should().BeFalse();
    }

    [Theory]
    [InlineData("<PackageReference Include=\"Q\">", "  <Version Condition=\"'$(X)' == '1'\">1.0.0</Version>", "</PackageReference>")]   // a conditioned child
    [InlineData("<PackageReference Include=\"Q\">", "  <Version>1.0.0</Version>", "")]                                                  // never closes
    [InlineData("<PackageReference Include=\"Q\" Version=\"1.0.0\">", "  <Version>2.0.0</Version>", "</PackageReference>")]             // metadata twice
    [InlineData("<PackageReference Include=\"Q\" junk />", "", "")]                                                                      // not an attribute
    public void MsBuild_refuses_an_item_it_cannot_read_value_by_value(string first, string second, string third)
    {
        Format("msbuild").Read(Lines([.. new[] { first, second, third }.Where(l => l.Length > 0)])).Should().BeNull();
    }

    [Fact]
    public void MsBuild_writes_a_property_value_and_refuses_a_property_without_one()
    {
        var format = Format("msbuild");
        var property = Entry("msbuild", "  <Version>1.0.0</Version>");

        format.Write(property, new Dictionary<string, string?> { [ManifestEntry.ScalarValue] = "2.0.0" }).Should().Equal(Lines("  <Version>2.0.0</Version>"));
        format.Write(property, new Dictionary<string, string?> { [ManifestEntry.ScalarValue] = null }).Should().BeNull();
        format.Write(property, new Dictionary<string, string?>()).Should().BeNull();
    }

    [Fact]
    public void MsBuild_removes_a_child_value_and_refuses_to_add_one_to_an_item_with_children()
    {
        var format = Format("msbuild");
        var child = Entry("msbuild", "<PackageReference Include=\"Q\">", "  <Version>1.0.0</Version>", "  <PrivateAssets>all</PrivateAssets>", "</PackageReference>");

        format.Write(child, new Dictionary<string, string?> { ["PrivateAssets"] = null })
            .Should().Equal(Lines("<PackageReference Include=\"Q\">", "  <Version>1.0.0</Version>", "</PackageReference>"));
        format.Write(child, new Dictionary<string, string?> { ["IncludeAssets"] = "runtime" }).Should().BeNull();
    }

    [Theory]
    [InlineData("1.0.0-rc", "1.0.0-rc.1", -1)]    // a shorter prerelease of the same start ranks below
    [InlineData("1.0.0-RC.1", "1.0.0-rc.1", 0)]   // identifiers compare without case
    public void Prerelease_identifiers_compare_part_by_part_then_by_count(string a, string b, int expected)
    {
        Math.Sign(SemanticVersion.Compare(a, b) ?? 99).Should().Be(expected);
        SemanticVersion.Parse(a)!.CompareTo(null).Should().Be(1);
    }

    [Fact]
    public void Json_keeps_a_blank_line_with_the_entry_after_it_and_refuses_a_value_it_cannot_write()
    {
        var format = Format("npm");
        var section = format.Read(Lines("    \"a\": \"^1.0.0\",", "", "    \"b\": \"^2.0.0\""))!;

        section.Entries.Should().HaveCount(2);
        section.Entries[1].Leading.Should().Equal(Lines(""));
        format.Write(section.Entries[0], new Dictionary<string, string?> { [ManifestEntry.ScalarValue] = "^1.0.0\", \"evil\": \"1" }).Should().BeNull();
        format.CompareVersions(section.Entries[0], ManifestEntry.ScalarValue, "^1.0.0", "^1.1.0", []).HasValue.Should().BeFalse("nothing above names an object");
    }

    [Theory]
    [InlineData("1.*")]          // floating
    [InlineData("[1.0,2.0)")]    // a range
    [InlineData("$(QVersion)")]  // a property
    public void MsBuild_gives_no_order_to_a_version_it_cannot_parse(string version)
    {
        var entry = Entry("msbuild", "<PackageReference Include=\"Q\" Version=\"1.0.0\" />");

        Format("msbuild").CompareVersions(entry, "Version", version, "2.0.0", []).HasValue.Should().BeFalse();
    }

    [Fact]
    public void MsBuild_writes_a_value_back_in_place_keeping_the_rest_of_the_text()
    {
        var format = Format("msbuild");
        var single = Entry("msbuild", "    <PackageReference  Include=\"Q\"   Version=\"1.0.0\" />");
        var child = Entry("msbuild", "<PackageReference Include=\"Q\">", "  <Version>1.0.0</Version>", "</PackageReference>");

        format.Write(single, new Dictionary<string, string?> { ["Version"] = "2.0.0" })
            .Should().Equal(Lines("    <PackageReference  Include=\"Q\"   Version=\"2.0.0\" />"));
        format.Write(child, new Dictionary<string, string?> { ["Version"] = "2.0.0" })
            .Should().Equal(Lines("<PackageReference Include=\"Q\">", "  <Version>2.0.0</Version>", "</PackageReference>"));
        format.Write(single, new Dictionary<string, string?> { ["Version"] = "2.0.0", ["PrivateAssets"] = "all" })
            .Should().Equal(Lines("    <PackageReference  Include=\"Q\"   Version=\"2.0.0\" PrivateAssets=\"all\" />"));
        format.Write(single, new Dictionary<string, string?> { ["Version"] = "2.0.0\" Evil=\"1" }).Should().BeNull();
    }

    // --- json formats: one "name": "value" line per entry ---

    [Theory]
    [InlineData("npm")]
    [InlineData("composer")]
    public void Json_reads_a_name_value_line_and_refuses_anything_else(string format)
    {
        var entry = Entry(format, "    \"laravel/framework\": \"^13.17\",");
        entry.Scalar.Should().BeTrue();
        entry.Values[ManifestEntry.ScalarValue].Should().Be("^13.17");

        Format(format).Read(Lines("  \"scripts\": {")).Should().BeNull();
        Format(format).Read(Lines("  },")).Should().BeNull();
        Format(format).Read(Lines("  \"files\": [\"dist\"],")).Should().BeNull();
    }

    [Theory]
    [InlineData("dependencies", true)]
    [InlineData("devDependencies", true)]
    [InlineData("peerDependencies", true)]
    [InlineData("optionalDependencies", true)]
    [InlineData("scripts", false)]
    [InlineData("engines", false)]   // the platform (node), on a release line the line itself
    public void Npm_orders_a_range_only_inside_a_dependency_object(string @object, bool ordered)
    {
        var entry = Entry("npm", "    \"q\": \"^1.2.0\",");
        var before = Lines("{", "  \"name\": \"@acme/lib\",", $"  \"{@object}\": {{", "    \"a\": \"^1.0.0\",");

        (Format("npm").CompareVersions(entry, ManifestEntry.ScalarValue, "^1.2.0", "^1.10.0", before) is < 0).Should().Be(ordered);
    }

    [Fact]
    public void Npm_gives_no_order_at_the_top_level_or_after_the_dependency_object_closed()
    {
        var entry = Entry("npm", "  \"version\": \"15.0.4\",");

        // The package's own version: the census of the MintPlayer repositories found it the most frequent conflict.
        Format("npm").CompareVersions(entry, ManifestEntry.ScalarValue, "15.0.4", "15.0.5", Lines("{", "  \"name\": \"@acme/lib\","))
            .HasValue.Should().BeFalse();
        Format("npm").CompareVersions(entry, ManifestEntry.ScalarValue, "15.0.4", "15.0.5", Lines("{", "  \"dependencies\": {", "    \"a\": \"^1.0.0\"", "  },"))
            .HasValue.Should().BeFalse();
    }

    [Theory]
    [InlineData("^1.2.0", "^1.10.0", -1)]
    [InlineData("~1.3.0", "~1.2.9", 1)]
    [InlineData("1.3.0", "1.2.9", 1)]          // an exact pin
    [InlineData("^1.2.0", "~1.3.0", null)]     // two operators mean two intents
    [InlineData(">=1.0.0", "^2.0.0", null)]    // a comparator range has no single version
    [InlineData("^1.0.0 || ^2.0.0", "^2.0.0", null)]
    [InlineData("latest", "^2.0.0", null)]
    public void Npm_orders_ranges_of_one_operator_only(string a, string b, int? expected)
    {
        var entry = Entry("npm", "    \"q\": \"^1.2.0\",");

        var compared = Format("npm").CompareVersions(entry, ManifestEntry.ScalarValue, a, b, Lines("{", "  \"dependencies\": {"));
        (compared is { } c ? Math.Sign(c) : (int?)null).Should().Be(expected);
    }

    [Theory]
    [InlineData("php")]
    [InlineData("ext-intl")]
    [InlineData("lib-curl")]
    public void Composer_platform_requirements_have_no_order(string name)
    {
        var entry = Entry("composer", $"        \"{name}\": \"^8.3\",");

        Format("composer").CompareVersions(entry, ManifestEntry.ScalarValue, "^8.3", "^8.4", Lines("{", "    \"require\": {")).HasValue.Should().BeFalse();
    }

    [Fact]
    public void Composer_orders_a_package_inside_require_dev()
    {
        var entry = Entry("composer", "        \"laravel/pint\": \"^1.27\",");

        Format("composer").CompareVersions(entry, ManifestEntry.ScalarValue, "^1.27", "^1.28", Lines("{", "    \"require-dev\": {"))
            .Should().BeNegative();
        Format("composer").CompareVersions(entry, ManifestEntry.ScalarValue, "^1.27", "^1.28", Lines("{", "    \"autoload\": {"))
            .HasValue.Should().BeFalse();
    }

    [Fact]
    public void Json_writes_a_value_back_keeping_its_comma()
    {
        var entry = Entry("npm", "    \"q\": \"^1.2.0\",");

        Format("npm").Write(entry, new Dictionary<string, string?> { [ManifestEntry.ScalarValue] = "^1.10.0" }).Should().Equal(Lines("    \"q\": \"^1.10.0\","));
    }

    [Fact]
    public void Json_joins_merged_entries_with_the_commas_of_a_valid_object()
    {
        var format = Format("npm");
        var ours = format.Read(Lines("    \"a\": \"^1.0.0\",", "    \"m\": \"^2.0.0\""))!;
        var theirs = format.Read(Lines("    \"a\": \"^1.0.0\",", "    \"p\": \"^3.0.0\""))!;

        // Ours' last entry has no comma (the object closes after the region), so the merged last one has none either.
        format.Join([ours.Entries[0], ours.Entries[1], theirs.Entries[1]], [], ours, theirs)
            .Should().Equal(Lines("    \"a\": \"^1.0.0\",", "    \"m\": \"^2.0.0\",", "    \"p\": \"^3.0.0\""));
    }
}
