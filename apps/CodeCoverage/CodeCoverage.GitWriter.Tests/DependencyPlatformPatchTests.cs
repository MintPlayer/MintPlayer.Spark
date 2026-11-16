using CodeCoverage.Git.Dependencies;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #613 items 2 and 3: the <c>dependencies</c> profile on a platform group's patch, written by real <c>git diff</c>.
/// Before #613, the request carried only group items, so every SDK adapter edit was <c>outside-planned-files</c> or
/// <c>denied-path</c> (a Directory.Build.props), and a NuGet bump in Directory.Build.props refused the whole patch.
/// </summary>
public class DependencyPlatformPatchTests : IDisposable
{
    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private const string Group = "platform:dotnet 11.0";

    private static readonly PlanPlatform Dotnet11 = new()
    {
        Sdk = "dotnet", FromChannel = "10.0", ToChannel = "11.0", ToVersion = "11.0.0", SdkVersion = "11.0.100",
    };

    private static DependencyPatchRequest Request(PlanPlatform move, params DependencyPlatformLocation[] locations)
        => new([]) { Platforms = [new DependencyPlatformMove(Group, move, locations)], ClaimedAppliedGroups = [Group] };

    private static DependencyPlatformLocation Location(string file, string kind, string value) => new("dotnet", file, kind, value);

    private Task<DependencyPatchVerdict> Validate(string baseCommit, string afterCommit, DependencyPatchRequest request)
        => DependencyPatchValidator.ValidateAsync(repo.Diff(baseCommit, afterCommit), request, repo.Tree(baseCommit));

    private const string BuildProps = "<Project>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n"
                                      + "  <ItemGroup>\n    <PackageReference Include=\"MintPlayer.Versioning.MSBuild\" Version=\"1.0.1\" PrivateAssets=\"all\" />\n  </ItemGroup>\n</Project>\n";

    // ═══ Directory.Build.props: strict substitutions (gap A, owner 2026-11-09) ══════════════════════

    [Fact]
    public async Task A_target_framework_move_in_Directory_Build_props_is_accepted()
    {
        var before = repo.Commit(("Directory.Build.props", BuildProps));
        var after = repo.Change(before, ("Directory.Build.props", BuildProps.Replace("net10.0", "net11.0", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location("Directory.Build.props", "tfm", "net10.0")));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal(Group);
    }

    [Fact]
    public async Task Strategy_add_renames_the_element_on_a_single_target_project()
    {
        var before = repo.Commit(("Directory.Build.props", BuildProps));
        var after = repo.Change(before, ("Directory.Build.props", BuildProps.Replace("<TargetFramework>net10.0</TargetFramework>",
            "<TargetFrameworks>net11.0;net10.0</TargetFrameworks>", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11 with { Strategy = "add" }, Location("Directory.Build.props", "tfm", "net10.0")));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
    }

    [Fact]
    public async Task A_nuget_bump_in_Directory_Build_props_is_a_planned_substitution()
    {
        var before = repo.Commit(("Directory.Build.props", BuildProps));
        var after = repo.Change(before, ("Directory.Build.props", BuildProps.Replace("Version=\"1.0.1\"", "Version=\"1.1.0\"", StringComparison.Ordinal)));
        var entry = new DependencyPlanEntry("MintPlayer.Versioning.MSBuild", "Directory.Build.props", "1.0.1", "1.1.0", "MintPlayer.Versioning.MSBuild", "nuget");

        var verdict = await DependencyPatchValidator.ValidateAsync(repo.Diff(before, after), new DependencyPatchRequest([entry]), repo.Tree(before));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal("MintPlayer.Versioning.MSBuild");
    }

    [Fact]
    public async Task An_import_added_beside_a_planned_move_is_refused()
    {
        var before = repo.Commit(("Directory.Build.props", BuildProps));
        var after = repo.Change(before, ("Directory.Build.props", BuildProps.Replace("net10.0", "net11.0", StringComparison.Ordinal)
            .Replace("</Project>", "  <Import Project=\"evil.targets\" />\n</Project>", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location("Directory.Build.props", "tfm", "net10.0")));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.NotASubstitution);
    }

    [Theory]
    [InlineData("<TargetFramework>net11.0</TargetFramework><Import Project=\"evil.targets\" />")]
    [InlineData("<TargetFramework>net11.0\" Condition=\"true</TargetFramework>")]
    [InlineData("<TargetFramework>net11.0</TargetFramework> <!-- -->")]
    public async Task A_move_that_changes_anything_else_on_the_line_is_refused(string line)
    {
        var before = repo.Commit(("Directory.Build.props", BuildProps));
        var after = repo.Change(before, ("Directory.Build.props", BuildProps.Replace("<TargetFramework>net10.0</TargetFramework>", line, StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location("Directory.Build.props", "tfm", "net10.0")));

        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_Directory_Build_props_edit_with_no_planned_change_there_is_denied()
    {
        var before = repo.Commit(("Directory.Build.props", BuildProps));
        var after = repo.Change(before, ("Directory.Build.props", BuildProps.Replace("enable", "disable", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location("src/App/App.csproj", "tfm", "net10.0")));

        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
        verdict.Paths.Should().Equal("Directory.Build.props");
    }

    // ═══ The move itself ════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_value_that_does_not_name_the_target_is_refused()
    {
        var before = repo.Commit(("Directory.Build.props", BuildProps));
        var after = repo.Change(before, ("Directory.Build.props", BuildProps.Replace("net10.0", "net12.0", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location("Directory.Build.props", "tfm", "net10.0")));

        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_file_the_lookup_did_not_report_admits_nothing()
    {
        const string csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
        var before = repo.Commit(("src/App/App.csproj", csproj), ("src/Other/Other.csproj", csproj));
        var after = repo.Change(before, ("src/App/App.csproj", csproj.Replace("net10.0", "net11.0", StringComparison.Ordinal)),
            ("src/Other/Other.csproj", csproj.Replace("net10.0", "net11.0", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location("src/App/App.csproj", "tfm", "net10.0")));

        verdict.Reason.Should().Be(DependencyPatchReasons.OutsidePlannedFiles);
        verdict.Paths.Should().Equal("src/Other/Other.csproj");
    }

    [Fact]
    public async Task The_sdk_version_of_global_json_moves_to_the_planned_sdk()
    {
        const string globalJson = "{\n  \"sdk\": {\n    \"version\": \"10.0.100\",\n    \"rollForward\": \"latestFeature\"\n  }\n}\n";
        var before = repo.Commit(("global.json", globalJson));
        var after = repo.Change(before, ("global.json", globalJson.Replace("10.0.100", "11.0.100", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location("global.json", "global.json", "10.0.100")));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal(Group);
    }

    [Fact]
    public async Task Companions_that_ride_without_the_sdk_move_are_a_partial_group()
    {
        const string csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Microsoft.Extensions.Hosting\" Version=\"10.0.0\" />\n  </ItemGroup>\n</Project>\n";
        var before = repo.Commit(("src/App/App.csproj", csproj), ("Directory.Build.props", BuildProps));
        var after = repo.Change(before, ("src/App/App.csproj", csproj.Replace("10.0.0", "11.0.0", StringComparison.Ordinal)));
        var rider = new DependencyPlanEntry("Microsoft.Extensions.Hosting", "src/App/App.csproj", "10.0.0", "11.0.0", Group, "nuget");
        var request = new DependencyPatchRequest([rider])
        {
            Platforms = [new DependencyPlatformMove(Group, Dotnet11, [Location("Directory.Build.props", "tfm", "net10.0")])],
            ClaimedAppliedGroups = [Group],
        };

        var verdict = await Validate(before, after, request);

        verdict.Reason.Should().Be(DependencyPatchReasons.PartialGroup);
        verdict.Detail.Should().Contain("its SDK move");
    }

    // ═══ Workflows (R15 carve-out plus the YAML comparison) ═════════════════════════════════════════

    private const string Workflow = "name: ci\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    strategy:\n      matrix:\n        dotnet: [ '8.0.x', '10.0.x' ]\n"
                                    + "    steps:\n      - uses: actions/setup-dotnet@v4\n        with:\n          dotnet-version: 10.0.x\n      - run: echo 10.0.x\n";

    [Fact]
    public async Task A_setup_action_version_and_a_flow_matrix_entry_move()
    {
        var before = repo.Commit((".github/workflows/ci.yml", Workflow));
        var after = repo.Change(before, (".github/workflows/ci.yml", Workflow
            .Replace("dotnet-version: 10.0.x", "dotnet-version: 11.0.x", StringComparison.Ordinal)
            .Replace("'10.0.x' ]", "'11.0.x' ]", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11,
            Location(".github/workflows/ci.yml", "setup-action", "10.0.x"), Location(".github/workflows/ci.yml", "matrix", "10.0.x")));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal(Group);
        verdict.RequiresWorkflowsPermission.Should().BeTrue();
    }

    [Fact]
    public async Task A_run_script_holding_the_value_is_not_a_platform_location()
    {
        var before = repo.Commit((".github/workflows/ci.yml", Workflow));
        var after = repo.Change(before, (".github/workflows/ci.yml", Workflow.Replace("echo 10.0.x", "echo 11.0.x", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location(".github/workflows/ci.yml", "setup-action", "10.0.x")));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(Jobs.PatchReasons.InvalidContent);
    }

    // ═══ platform-locations-PRD M3/M2: a multi-line setup input, an older Docker stage ═════════════════

    [Fact]
    public async Task One_line_of_a_multi_line_setup_input_moves()
    {
        const string workflow = "name: ci\non: push\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/setup-dotnet@v4\n        with:\n"
                                + "          dotnet-version: |\n            10.0.x\n            11.0.100-rc.1.26425.128\n      - run: |\n          echo 11.0.100-rc.1.26425.128\n";
        var before = repo.Commit((".github/workflows/_build.yml", workflow));
        var move = Dotnet11 with { FromChannel = "11.0" };
        var request = Request(move, Location(".github/workflows/_build.yml", "setup-action", "11.0.100-rc.1.26425.128"));

        var setup = repo.Change(before, (".github/workflows/_build.yml", workflow.Replace("            11.0.100-rc.1.26425.128\n", "            11.0.100\n", StringComparison.Ordinal)));
        var script = repo.Change(before, (".github/workflows/_build.yml", workflow.Replace("echo 11.0.100-rc.1.26425.128", "echo 11.0.100", StringComparison.Ordinal)));

        var accepted = await Validate(before, setup, request);
        accepted.Outcome.Should().Be(DependencyPatchOutcome.Accepted, $"{accepted.Reason}: {accepted.Detail}");
        (await Validate(before, script, request)).Reason.Should().Be(Jobs.PatchReasons.InvalidContent, "a run: block is no setup input");
    }

    [Fact]
    public async Task An_older_Docker_stage_follows_the_move()
    {
        const string dockerfile = "FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:9.0\n";
        var before = repo.Commit(("content/Web/Dockerfile", dockerfile));
        var after = repo.Change(before, ("content/Web/Dockerfile", dockerfile.Replace(":9.0", ":11.0", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location("content/Web/Dockerfile", "docker", "9.0")));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
    }

    // ═══ platform-locations-PRD M4 / P9: the defining line of a computed TFM in a *.props ════════════════

    private const string VersionsProps = "<Project>\n  <PropertyGroup>\n    <AspNetCoreMajorVersion>10</AspNetCoreMajorVersion>\n    <AspNetCoreMinorVersion>0</AspNetCoreMinorVersion>\n"
                                         + "    <DefaultNetCoreTargetFramework>net$(AspNetCoreMajorVersion).$(AspNetCoreMinorVersion)</DefaultNetCoreTargetFramework>\n"
                                         + "    <LangVersion>10</LangVersion>\n  </PropertyGroup>\n</Project>\n";

    private Task<DependencyPatchVerdict> ValidateVersionsProps(string after, string kind = "tfm-property", string value = "10")
    {
        var before = repo.Commit(("eng/Versions.props", VersionsProps));
        var changed = repo.Change(before, ("eng/Versions.props", after));
        return Validate(before, changed, Request(Dotnet11, Location("eng/Versions.props", kind, value)));
    }

    [Fact]
    public async Task The_reported_property_s_bare_major_moves_in_a_props_file()
    {
        var verdict = await ValidateVersionsProps(VersionsProps.Replace("<AspNetCoreMajorVersion>10<", "<AspNetCoreMajorVersion>11<", StringComparison.Ordinal));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal(Group);
    }

    [Fact]
    public async Task A_property_holding_the_channel_moves_its_channel()
    {
        const string props = "<Project>\n  <PropertyGroup>\n    <NetCurrent>net10.0</NetCurrent>\n  </PropertyGroup>\n</Project>\n";
        var before = repo.Commit(("eng/Common.props", props));
        var after = repo.Change(before, ("eng/Common.props", props.Replace("net10.0", "net11.0", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Request(Dotnet11, Location("eng/Common.props", "tfm-property", "10.0")));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
    }

    [Theory]
    [InlineData("<AspNetCoreMajorVersion>10<", "<AspNetCoreMajorVersion>12<", "not the target's major")]
    [InlineData("<AspNetCoreMinorVersion>0<", "<AspNetCoreMinorVersion>1<", "another property")]
    [InlineData("<AspNetCoreMajorVersion>10</AspNetCoreMajorVersion>", "<AspNetCoreMajorVersion Condition=\"true\">11</AspNetCoreMajorVersion>", "a condition added")]
    [InlineData("<AspNetCoreMajorVersion>10</AspNetCoreMajorVersion>", "<AspNetCoreMajorVersion>11</AspNetCoreMajorVersion><Import Project=\"x.targets\" />", "an import beside it")]
    [InlineData("<DefaultNetCoreTargetFramework>net$(AspNetCoreMajorVersion)", "<DefaultNetCoreTargetFramework>net11", "the expression")]
    public async Task Anything_but_the_one_token_in_a_props_file_is_refused(string from, string to, string because)
    {
        var verdict = await ValidateVersionsProps(VersionsProps.Replace(from, to, StringComparison.Ordinal));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected, because);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange, because);
    }

    [Fact]
    public async Task A_plain_property_of_the_same_value_is_the_accepted_residue()
    {
        // P9's check is the line's shape and the old/new values, not the property's name (the location carries none):
        // <LangVersion>10</LangVersion> -> 11 passes too. One numeric token, no structure; recorded in the PRD's review.
        var verdict = await ValidateVersionsProps(VersionsProps.Replace("<LangVersion>10<", "<LangVersion>11<", StringComparison.Ordinal));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted);
    }

    [Fact]
    public async Task Only_a_tfm_property_location_opens_a_props_file()
    {
        var verdict = await ValidateVersionsProps(VersionsProps.Replace("<AspNetCoreMajorVersion>10<", "<AspNetCoreMajorVersion>11<", StringComparison.Ordinal),
            kind: "tfm", value: "10");

        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
    }

    // ═══ platform-locations-PRD M6 / P10: an action's runs.using ═══════════════════════════════════════

    private static readonly PlanPlatform Node26 = new() { Sdk = "node", FromChannel = "24", ToChannel = "26", ToVersion = "26.0.0", ActionRuntimes = ["node20", "node24"] };

    private async Task<DependencyPatchVerdict> ValidateAction(string to, PlanPlatform move)
    {
        const string action = "name: Delay\nruns:\n  using: 'node20'\n  main: dist/index.js\n";
        var before = repo.Commit(("delay/action.yml", action));
        var changed = repo.Change(before, ("delay/action.yml", action.Replace("node20", to, StringComparison.Ordinal)));
        return await Validate(before, changed, new DependencyPatchRequest([])
        {
            Platforms = [new DependencyPlatformMove("platform:node 26", move, [new DependencyPlatformLocation("node", "delay/action.yml", "action-runtime", "node20")])],
            ClaimedAppliedGroups = ["platform:node 26"],
        });
    }

    [Fact]
    public async Task An_action_runtime_moves_to_a_runtime_GitHub_runs_below_the_target()
    {
        // node24, not node26: the token does not name the target, and is admitted because the plan lists it.
        var verdict = await ValidateAction("node24", Node26);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
    }

    [Theory]
    [InlineData("node26", "the target, which GitHub does not run (not in actionRuntimes)")]
    [InlineData("node24\n  pre: x.js", "anything beside the token")]
    public async Task An_action_runtime_outside_the_plan_s_list_is_refused(string to, string because)
    {
        var verdict = await ValidateAction(to, Node26);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected, because);
    }

    [Fact]
    public async Task Without_the_runtime_list_an_action_runtime_is_refused()
    {
        var verdict = await ValidateAction("node24", Node26 with { ActionRuntimes = null });

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
    }

    // ═══ platform-locations-PRD M5 / P8: a template.json framework choice ════════════════════════════

    private const string TemplatePath = "content/Web/.template.config/template.json";

    private const string TemplateJson = """
        {
          "symbols": {
            "Framework": {
              "datatype": "choice",
              "choices": [
                { "choice": "net10.0", "description": "Target net10.0" }
              ],
              "replaces": "net10.0",
              "defaultValue": "net10.0"
            }
          }
        }

        """;

    private async Task<DependencyPatchVerdict> ValidateTemplate(string after)
    {
        var before = repo.Commit((TemplatePath, TemplateJson));
        var changed = repo.Change(before, (TemplatePath, after));
        return await Validate(before, changed, Request(Dotnet11, Location(TemplatePath, "template-framework", "net10.0")));
    }

    [Fact]
    public async Task The_template_framework_tokens_move()
    {
        var verdict = await ValidateTemplate(TemplateJson.Replace("net10.0", "net11.0", StringComparison.Ordinal));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
    }

    [Fact]
    public async Task A_second_choice_added_to_the_template_is_refused()
    {
        // strategy add is reported, never written: an added choice is JSON structure the line rule refuses.
        var verdict = await ValidateTemplate(TemplateJson.Replace("""{ "choice": "net10.0", "description": "Target net10.0" }""",
            """{ "choice": "net11.0", "description": "Target net11.0" }, { "choice": "net10.0", "description": "Target net10.0" }""", StringComparison.Ordinal));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
    }
}
