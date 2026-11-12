using CodeCoverage.MergeQueue;
using CodeCoverage.Onboarding;
using Xunit;

namespace CodeCoverage.Tests.Onboarding;

/// <summary>
/// #638 D15: an older .NET line cut from a commit that also targets a higher major gets a narrowing pull request. The
/// shape is Tools #55, done by hand: <c>net10.0;net11.0</c> on release/dotnet-10.x.
/// </summary>
public class LineNarrowingTests
{
    private const string Csproj = "src/Hsts/Hsts.csproj";

    private static string Project(string tfm, string framework = "11.0.0", string? conditional = null)
        => "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
           + "  <PropertyGroup>\n"
           + $"    <TargetFrameworks>{tfm}</TargetFrameworks>\n"
           + "  </PropertyGroup>\n"
           + "  <ItemGroup>\n"
           + $"    <PackageReference Include=\"Microsoft.Extensions.Options\" Version=\"{framework}\" />\n"
           + "    <PackageReference Include=\"QRCoder\" Version=\"1.6.0\" />\n"
           + "  </ItemGroup>\n"
           + (conditional is null ? "" : $"  <ItemGroup Condition=\"{conditional}\">\n    <Compile Remove=\"Net11Only.cs\" />\n  </ItemGroup>\n")
           + "</Project>\n";

    private static string GlobalJson(string sdk) => $"{{\n  \"sdk\": {{\n    \"version\": \"{sdk}\",\n    \"rollForward\": \"latestFeature\"\n  }}\n}}\n";

    [Fact]
    public void D15_the_higher_major_is_dropped_and_the_sdk_and_framework_packages_follow_the_line()
    {
        var narrowing = LineNarrowing.Plan(10, new Dictionary<string, string>
        {
            [Csproj] = Project("net10.0;net11.0", conditional: "'$(TargetFramework)' == 'net11.0'"),
            ["global.json"] = GlobalJson("11.0.100"),
            ["src/Hsts/HstsMiddleware.cs"] = "namespace Hsts;\n#if NET11_0_OR_GREATER\nusing System.Net.Http.Features;\n#endif\n",
            ["src/Hsts/Other.cs"] = "#if NET8_0_OR_GREATER\n#endif\n",
        }, latestSdk: "10.0.203", latestRuntime: "10.0.7");

        narrowing.Files.Should().BeEquivalentTo([
            new GitWriterFile(Csproj, Project("net10.0", framework: "10.0.7", conditional: "'$(TargetFramework)' == 'net11.0'")),
            new GitWriterFile("global.json", GlobalJson("10.0.203")),
        ]);
        narrowing.Changes.Should().Equal([
            $"`{Csproj}`: TargetFrameworks `net10.0;net11.0` → `net10.0`",
            $"`{Csproj}`: Microsoft.Extensions.Options `11.0.0` → `10.0.7`",
            "`global.json`: SDK `11.0.100` → `10.0.203`",
        ]);
        narrowing.Conditionals.Should().Equal([
            $"`{Csproj}:9`: `Condition=\"'$(TargetFramework)' == 'net11.0'\"`",
            "`src/Hsts/HstsMiddleware.cs:2`: `#if NET11_0_OR_GREATER`",
        ]);
    }

    [Fact]
    public void D15_a_line_that_targets_nothing_higher_needs_no_pull_request()
    {
        var narrowing = LineNarrowing.Plan(10, new Dictionary<string, string>
        {
            [Csproj] = Project("netstandard2.0;net10.0", framework: "10.0.7"),
            ["global.json"] = GlobalJson("10.0.203"),
        }, latestSdk: "10.0.203", latestRuntime: "10.0.7");

        narrowing.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void D15_a_project_only_on_the_higher_major_moves_to_the_line_and_an_unknown_sdk_is_left_for_a_person()
    {
        var narrowing = LineNarrowing.Plan(10, new Dictionary<string, string>
        {
            [Csproj] = Project("net11.0"),
            ["global.json"] = GlobalJson("11.0.100"),
        }, latestSdk: null, latestRuntime: null);

        narrowing.Files.Should().BeEquivalentTo([new GitWriterFile(Csproj, Project("net10.0", framework: "10.0.0"))]);
        narrowing.Notes.Should().Equal(["`global.json` pins SDK `11.0.100`; the latest .NET 10 SDK is not known yet, so set it by hand."]);
    }

    [Fact]
    public void D15_the_body_lists_the_changes_and_the_conditionals_to_resolve()
    {
        var narrowing = LineNarrowing.Plan(10, new Dictionary<string, string>
        {
            [Csproj] = Project("net10.0;net11.0", framework: "10.0.7"),
            ["src/Hsts/HstsMiddleware.cs"] = "#if NET11_0_OR_GREATER\n#endif\n",
        }, latestSdk: "10.0.203", latestRuntime: "10.0.7");

        narrowing.Body("release/dotnet-10.x").Should().Be(
            "`release/dotnet-10.x` was cut from a commit that also targets a higher .NET major. This pull request narrows it to .NET 10:\n\n"
            + $"- `{Csproj}`: TargetFrameworks `net10.0;net11.0` → `net10.0`\n"
            + "\nThese conditionals name a higher .NET and are now dead or wrong on this line; resolve them before merging:\n\n"
            + "- `src/Hsts/HstsMiddleware.cs:1`: `#if NET11_0_OR_GREATER`\n"
            + "\nThe projects keep `<TargetFrameworks>` with one value, so MSBuild logic that reads `$(TargetFrameworks)` keeps working. "
            + "Switching them to `<TargetFramework>` changes what that logic sees.\n");
    }
}
