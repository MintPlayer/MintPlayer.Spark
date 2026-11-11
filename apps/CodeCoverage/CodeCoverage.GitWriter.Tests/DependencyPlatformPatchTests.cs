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
}
