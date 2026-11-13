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

    /// <summary>SpaServices' pull-request.yml step on release/dotnet-10.x as the cut left it (PR #110, 2026-11-12): global.json
    /// narrowed to 10.0.404 while this still installed the 11 rc, so `dotnet restore` found no SDK (exit 155).</summary>
    private static string SetupDotnet(string version)
        => "    - name: Setup .NET Core\n      uses: actions/setup-dotnet@v6\n      with:\n        dotnet-version: " + version + "\n";

    /// <summary>SpaServices' global.json (Arcade's layout): <c>tools.dotnet</c> pins the SDK a second time.</summary>
    private static string ArcadeGlobalJson(string sdk, string tools)
        => "{\n\t\"sdk\": {\n\t\t\"version\": \"" + sdk + "\",\n\t\t\"rollForward\": \"latestFeature\"\n\t},\n\t\"tools\": {\n\t\t\"dotnet\": \"" + tools
           + "\",\n\t\t\"runtimes\": {\n\t\t\t\"dotnet/x64\": [\n\t\t\t\t\"$(MicrosoftNETCoreAppInternalPackageVersion)\"\n\t\t\t]\n\t\t}\n\t}\n}\n";

    private const string Rc = "11.0.100-rc.1.26425.128";

    [Fact]
    public void D15_the_workflows_sdk_pins_and_the_tools_sdk_follow_the_line()
    {
        var narrowing = LineNarrowing.Plan(10, new Dictionary<string, string>
        {
            [Csproj] = Project("net10.0;net11.0", framework: "10.0.7"),
            ["global.json"] = ArcadeGlobalJson(Rc, Rc),
            [".github/workflows/pull-request.yml"] = SetupDotnet(Rc) + "\n" + SetupDotnet(Rc),
            [".github/workflows/build-any.yml"] = SetupDotnet("'11.0.x'"),
            [".github/workflows/legacy.yml"] = SetupDotnet("8.0.x"),
            [".github/workflows/dependency-apply.yml"] = "      - uses: actions/setup-dotnet@v6\n        with:\n          global-json-file: global.json\n",
        }, latestSdk: "10.0.404", latestRuntime: "10.0.7");

        narrowing.Files.Should().BeEquivalentTo([
            new GitWriterFile(Csproj, Project("net10.0", framework: "10.0.7")),
            new GitWriterFile("global.json", ArcadeGlobalJson("10.0.404", "10.0.404")),
            new GitWriterFile(".github/workflows/build-any.yml", SetupDotnet("'10.0.x'")),
            new GitWriterFile(".github/workflows/pull-request.yml", SetupDotnet("10.0.404") + "\n" + SetupDotnet("10.0.404")),
        ]);
        narrowing.Changes.Should().Equal([
            $"`{Csproj}`: TargetFrameworks `net10.0;net11.0` → `net10.0`",
            $"`global.json`: SDK `{Rc}` → `10.0.404`",
            $"`global.json`: tools.dotnet `{Rc}` → `10.0.404`",
            "`.github/workflows/build-any.yml:4`: dotnet-version `11.0.x` → `10.0.x`",
            $"`.github/workflows/pull-request.yml:4`: dotnet-version `{Rc}` → `10.0.404`",
            $"`.github/workflows/pull-request.yml:9`: dotnet-version `{Rc}` → `10.0.404`",
        ]);
    }

    [Fact]
    public void D15_a_workflow_pin_without_a_known_sdk_or_in_a_list_is_left_for_a_person()
    {
        var narrowing = LineNarrowing.Plan(10, new Dictionary<string, string>
        {
            [".github/workflows/pull-request.yml"] = SetupDotnet(Rc),
            [".github/workflows/matrix.yml"] = "      - uses: actions/setup-dotnet@v6\n        with:\n          dotnet-version: |\n            8.0.x\n            11.0.x\n",
            ["global.json"] = ArcadeGlobalJson(Rc, Rc),
        }, latestSdk: null, latestRuntime: null);

        narrowing.Files.Should().BeEmpty();
        narrowing.Notes.Should().Equal([
            $"`global.json` pins SDK `{Rc}`; the latest .NET 10 SDK is not known yet, so set it by hand.",
            $"`global.json` pins tools.dotnet `{Rc}`; the latest .NET 10 SDK is not known yet, so set it by hand.",
            "`.github/workflows/matrix.yml:3` lists SDKs above .NET 10 under `dotnet-version`; narrow the list by hand.",
            $"`.github/workflows/pull-request.yml:4` pins dotnet-version `{Rc}`; the latest .NET 10 SDK is not known yet, so set it by hand.",
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

    /// <summary>#651 D1: SpaServices #114 kept <c>&lt;LangVersion&gt;15&lt;/LangVersion&gt;</c> on release/dotnet-10.x, and the .NET 10
    /// compiler refused it (CS1617, run 43382800307). C# 15 is .NET 11's, so the line takes C# 14.</summary>
    [Fact]
    public void Issue651_D1_a_language_version_above_the_line_takes_the_lines_csharp_version()
    {
        static string WithLang(string tfm, string lang) => $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>{tfm}</TargetFrameworks>
                <LangVersion>{lang}</LangVersion>
              </PropertyGroup>
            </Project>
            """;

        static string Props(string lang) => $"""
            <Project>
              <PropertyGroup>
                <LangVersion>{lang}</LangVersion>
              </PropertyGroup>
            </Project>
            """;

        var narrowing = LineNarrowing.Plan(10, new Dictionary<string, string>
        {
            [Csproj] = WithLang("net10.0;net11.0", "15"),
            ["Directory.Build.props"] = Props("15.0"),
            ["src/Latest/Latest.csproj"] = WithLang("net10.0", "latest"),
            ["src/Old/Old.csproj"] = WithLang("net10.0", "13"),
        }, latestSdk: "10.0.404", latestRuntime: "10.0.7");

        narrowing.Files.Should().BeEquivalentTo([
            new GitWriterFile(Csproj, WithLang("net10.0", "14")),
            new GitWriterFile("Directory.Build.props", Props("14")),
        ]);
        narrowing.Changes.Should().Equal([
            "`Directory.Build.props`: LangVersion `15.0` → `14`",
            $"`{Csproj}`: TargetFrameworks `net10.0;net11.0` → `net10.0`",
            $"`{Csproj}`: LangVersion `15` → `14`",
        ]);
        // A file whose only problem is the language version still needs the pull request.
        LineNarrowing.TargetsAbove(10, Props("15")).Should().BeTrue();
        LineNarrowing.TargetsAbove(10, Props("preview")).Should().BeFalse();
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
