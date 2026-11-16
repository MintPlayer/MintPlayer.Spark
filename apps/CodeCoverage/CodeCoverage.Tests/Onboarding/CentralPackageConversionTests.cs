using CodeCoverage.Onboarding;
using Xunit;

namespace CodeCoverage.Tests.Onboarding;

/// <summary>
/// multi-target-dependency-split-PRD P7 (M7, M9-M11, M15): the "Convert to/from central package management" conversion of a
/// repository's MSBuild files, planned from the default branch's tree.
/// </summary>
public class CentralPackageConversionTests
{
    private const string Web = "src/Web/Web.csproj";
    private const string Lib = "src/Lib/Lib.csproj";
    private const string Props = "Directory.Packages.props";

    private static string Project(string tfm, string items)
        => $"<Project Sdk=\"Microsoft.NET.Sdk\">\n\n  <PropertyGroup>\n    <TargetFramework{(tfm.Contains(';') ? "s" : "")}>{tfm}</TargetFramework{(tfm.Contains(';') ? "s" : "")}>\n  </PropertyGroup>\n\n{items}</Project>\n";

    private static string Group(params string[] references)
        => "  <ItemGroup>\n" + string.Concat(references.Select(r => $"    {r}\n")) + "  </ItemGroup>\n\n";

    private static string Conditioned(string tfm, params string[] references)
        => $"  <ItemGroup Condition=\"'$(TargetFramework)' == '{tfm}'\">\n" + string.Concat(references.Select(r => $"    {r}\n")) + "  </ItemGroup>\n\n";

    private static string Ref(string id, string? version = null, string extra = "")
        => $"<PackageReference Include=\"{id}\"{(version is null ? "" : $" Version=\"{version}\"")}{extra} />";

    private static Dictionary<string, string> Files(params (string Path, string Text)[] files) => files.ToDictionary(f => f.Path, f => f.Text, StringComparer.Ordinal);

    private static string Written(CentralPackageConversion conversion, string path) => conversion.Files.Single(f => f.Path == path).Content!;

    // ─── detection (M15) ───

    [Fact]
    public void Detection_reads_ManagePackageVersionsCentrally_literally()
    {
        CentralPackageConversion.UsesCentral(Files((Web, Project("net11.0", Group(Ref("A", "1.0.0")))))).Should().BeFalse();
        CentralPackageConversion.UsesCentral(Files((Props, "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>\n")))
            .Should().BeTrue();
        CentralPackageConversion.UsesCentral(Files((Props, "<Project><PropertyGroup><ManagePackageVersionsCentrally>$(Central)</ManagePackageVersionsCentrally></PropertyGroup></Project>\n")))
            .HasValue.Should().BeFalse();
    }

    // ─── to CPM (M9, M11) ───

    [Fact]
    public void To_central_moves_every_version_into_Directory_Packages_props()
    {
        var conversion = CentralPackageConversion.ToCentral(Files(
            (Web, Project("net11.0", Group(Ref("Newtonsoft.Json", "13.0.3"), Ref("Serilog", "4.2.0", " PrivateAssets=\"all\"")))),
            (Lib, Project("net11.0", Group(Ref("Newtonsoft.Json", "13.0.3"))))));

        conversion.Refusals.Should().BeEmpty();
        Written(conversion, Web).Should().Be(Project("net11.0", Group(Ref("Newtonsoft.Json"), Ref("Serilog", extra: " PrivateAssets=\"all\""))));
        Written(conversion, Lib).Should().Be(Project("net11.0", Group(Ref("Newtonsoft.Json"))));
        Written(conversion, Props).Should().Be("<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n    <PackageVersion Include=\"Serilog\" Version=\"4.2.0\" />\n  </ItemGroup>\n</Project>\n");
        conversion.Changes.Should().BeEmpty();
    }

    /// <summary>M9 (owner: "Highest for all"): disagreeing versions become the highest, and each move is listed.</summary>
    [Fact]
    public void To_central_takes_the_highest_version_and_lists_each_move()
    {
        var conversion = CentralPackageConversion.ToCentral(Files(
            (Web, Project("net11.0", Group(Ref("Newtonsoft.Json", "13.0.1")))),
            (Lib, Project("net11.0", Group(Ref("Newtonsoft.Json", "13.0.3"))))));

        Written(conversion, Props).Should().Contain("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />");
        conversion.Changes.Should().Equal($"`Newtonsoft.Json` in `{Web}`: 13.0.1 → 13.0.3");
    }

    /// <summary>M11 (owner: "Conditioned PackageVersion"): per-TFM references become conditioned PackageVersion items; the
    /// project keeps one versionless reference.</summary>
    [Fact]
    public void To_central_turns_per_TFM_references_into_conditioned_PackageVersions()
    {
        const string fsg = "Microsoft.Extensions.FileSystemGlobbing";
        var conversion = CentralPackageConversion.ToCentral(Files(
            (Web, Project("net10.0;net11.0", Group(Ref("Other", "1.0.0")) + Conditioned("net10.0", Ref(fsg, "10.0.15")) + Conditioned("net11.0", Ref(fsg, "11.0.0"))))));

        conversion.Refusals.Should().BeEmpty();
        Written(conversion, Web).Should().Be(Project("net10.0;net11.0", Group(Ref("Other")) + Group(Ref(fsg))));
        Written(conversion, Props).Should().Be("<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n"
            + "  <ItemGroup>\n    <PackageVersion Include=\"Other\" Version=\"1.0.0\" />\n  </ItemGroup>\n"
            + $"  <ItemGroup Condition=\"'$(TargetFramework)' == 'net10.0'\">\n    <PackageVersion Include=\"{fsg}\" Version=\"10.0.15\" />\n  </ItemGroup>\n"
            + $"  <ItemGroup Condition=\"'$(TargetFramework)' == 'net11.0'\">\n    <PackageVersion Include=\"{fsg}\" Version=\"11.0.0\" />\n  </ItemGroup>\n</Project>\n");
    }

    [Fact]
    public void To_central_keeps_CRLF_line_endings()
    {
        var project = Project("net11.0", Group(Ref("A", "1.0.0"))).ReplaceLineEndings("\r\n");

        var conversion = CentralPackageConversion.ToCentral(Files((Web, project)));

        Written(conversion, Web).Should().Be(Project("net11.0", Group(Ref("A"))).ReplaceLineEndings("\r\n"));
        Written(conversion, Props).Should().Contain("\r\n");
    }

    [Theory]
    [InlineData(" Condition=\"'$(Configuration)' == 'Debug'\"")]   // a condition of its own
    [InlineData(" VersionOverride=\"1.0.0\"")]
    public void To_central_refuses_what_it_cannot_tell(string extra)
    {
        var conversion = CentralPackageConversion.ToCentral(Files((Web, Project("net11.0", Group(Ref("A", "1.0.0", extra))))));

        conversion.Files.Should().BeEmpty();
        conversion.Refusals.Should().ContainSingle().Which.Should().Contain(Web);
    }

    [Fact]
    public void To_central_refuses_a_repository_that_already_has_Directory_Packages_props()
    {
        var conversion = CentralPackageConversion.ToCentral(Files((Web, Project("net11.0", Group(Ref("A", "1.0.0")))), (Props, "<Project />\n")));

        conversion.Files.Should().BeEmpty();
        conversion.Refusals.Should().ContainSingle().Which.Should().Contain(Props);
    }

    // ─── from CPM (M10) ───

    private const string Central = "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n"
        + "  <ItemGroup>\n    <PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n    <PackageVersion Include=\"Serilog\" Version=\"4.2.0\" />\n  </ItemGroup>\n</Project>\n";

    /// <summary>M10 (owner: "Delete it if only versions"): every version is inlined, a VersionOverride winning, and a
    /// Directory.Packages.props that held only versions is deleted.</summary>
    [Fact]
    public void From_central_inlines_versions_and_deletes_a_versions_only_file()
    {
        var conversion = CentralPackageConversion.FromCentral(Files(
            (Props, Central),
            (Web, Project("net11.0", Group(Ref("Newtonsoft.Json"), Ref("Serilog", extra: " VersionOverride=\"4.0.0\""))))));

        conversion.Refusals.Should().BeEmpty();
        Written(conversion, Web).Should().Be(Project("net11.0", Group(Ref("Newtonsoft.Json", "13.0.3"), Ref("Serilog", "4.0.0"))));
        conversion.Files.Single(f => f.Path == Props).Delete.Should().BeTrue();
    }

    [Fact]
    public void From_central_keeps_a_file_that_holds_more_than_versions()
    {
        var props = Central.Replace("  </PropertyGroup>\n", "    <LangVersion>latest</LangVersion>\n  </PropertyGroup>\n", StringComparison.Ordinal);

        var conversion = CentralPackageConversion.FromCentral(Files((Props, props), (Web, Project("net11.0", Group(Ref("Serilog"))))));

        Written(conversion, Props).Should().Be("<Project>\n  <PropertyGroup>\n    <LangVersion>latest</LangVersion>\n  </PropertyGroup>\n</Project>\n");
    }

    [Fact]
    public void From_central_splits_a_reference_over_conditioned_PackageVersions()
    {
        const string fsg = "Microsoft.Extensions.FileSystemGlobbing";
        var props = "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n"
            + $"  <ItemGroup Condition=\"'$(TargetFramework)' == 'net10.0'\">\n    <PackageVersion Include=\"{fsg}\" Version=\"10.0.15\" />\n  </ItemGroup>\n"
            + $"  <ItemGroup Condition=\"'$(TargetFramework)' == 'net11.0'\">\n    <PackageVersion Include=\"{fsg}\" Version=\"11.0.0\" />\n  </ItemGroup>\n</Project>\n";

        var conversion = CentralPackageConversion.FromCentral(Files((Props, props), (Web, Project("net10.0;net11.0", Group(Ref(fsg))))));

        conversion.Refusals.Should().BeEmpty();
        Written(conversion, Web).Should().Be(Project("net10.0;net11.0", Conditioned("net10.0", Ref(fsg, "10.0.15")) + Conditioned("net11.0", Ref(fsg, "11.0.0"))));
    }

    [Fact]
    public void From_central_refuses_a_reference_without_a_PackageVersion()
    {
        var conversion = CentralPackageConversion.FromCentral(Files((Props, Central), (Web, Project("net11.0", Group(Ref("Unknown"))))));

        conversion.Files.Should().BeEmpty();
        conversion.Refusals.Should().ContainSingle().Which.Should().Contain("Unknown");
    }

    /// <summary>A round trip ends where it started.</summary>
    [Fact]
    public void To_then_from_central_is_the_identity()
    {
        var web = Project("net11.0", Group(Ref("Newtonsoft.Json", "13.0.3"), Ref("Serilog", "4.2.0", " PrivateAssets=\"all\"")));
        var to = CentralPackageConversion.ToCentral(Files((Web, web)));

        var from = CentralPackageConversion.FromCentral(to.Files.ToDictionary(f => f.Path, f => f.Content!, StringComparer.Ordinal));

        Written(from, Web).Should().Be(web);
        from.Files.Single(f => f.Path == Props).Delete.Should().BeTrue();
    }
}
