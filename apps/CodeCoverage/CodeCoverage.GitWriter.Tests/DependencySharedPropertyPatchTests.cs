using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// docs/refused_property_and_packagemanager_edits_PRD.md D3: SpaServices' <c>MintPlayer.FolderHasher.Targets</c> is referenced
/// as <c>Version="$(FolderHasherTargetsVersion)"</c> in the NodeServices csproj, and the value lives in the root
/// <c>Directory.Build.props</c>. The record's <c>packageFile</c> is the csproj, so the action's edit of the props file was
/// <c>denied-path</c> until the writer admitted the property files of a NuGet entry with a shared variable. Real
/// <c>git diff</c> on a real repository.
/// </summary>
public class DependencySharedPropertyPatchTests : IDisposable
{
    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private const string Project = "MintPlayer.AspNetCore.NodeServices/MintPlayer.AspNetCore.NodeServices.csproj";

    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n"
                                  + "    <PackageReference Include=\"MintPlayer.FolderHasher.Targets\" Version=\"$(FolderHasherTargetsVersion)\" PrivateAssets=\"all\" />\n"
                                  + "  </ItemGroup>\n</Project>\n";

    private static string Props(string value) => "<Project>\n  <PropertyGroup>\n    <LangVersion>preview</LangVersion>\n"
                                                 + $"    <FolderHasherTargetsVersion>{value}</FolderHasherTargetsVersion>\n  </PropertyGroup>\n</Project>\n";

    private static DependencyPlanEntry Entry(string? shared = "FolderHasherTargetsVersion")
        => new("MintPlayer.FolderHasher.Targets", Project, "11.0.0-rc.1", "11.0.0-rc.2", "MintPlayer.FolderHasher.Targets", "nuget") { SharedVariableName = shared };

    private async Task<DependencyPatchVerdict> Validate(string before, string after, params DependencyPlanEntry[] entries)
        => await DependencyPatchValidator.ValidateAsync(repo.Diff(before, after), new DependencyPatchRequest(entries), repo.Tree(before));

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("MintPlayer.AspNetCore.NodeServices/Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    public async Task The_property_in_a_Directory_file_above_the_project_is_a_planned_substitution(string propsFile)
    {
        var before = repo.Commit((Project, Csproj), (propsFile, Props("11.0.0-rc.1")));
        var after = repo.Change(before, (propsFile, Props("11.0.0-rc.2")));

        var verdict = await Validate(before, after, Entry());

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal("MintPlayer.FolderHasher.Targets");
    }

    [Fact]
    public async Task Without_a_shared_variable_the_props_edit_stays_refused()
    {
        var before = repo.Commit((Project, Csproj), ("Directory.Build.props", Props("11.0.0-rc.1")));
        var after = repo.Change(before, ("Directory.Build.props", Props("11.0.0-rc.2")));

        var verdict = await Validate(before, after, Entry(shared: null));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
    }

    [Fact]
    public async Task A_props_file_beside_another_directory_is_not_the_project_s()
    {
        var before = repo.Commit((Project, Csproj), ("Other/Directory.Build.props", Props("11.0.0-rc.1")));
        var after = repo.Change(before, ("Other/Directory.Build.props", Props("11.0.0-rc.2")));

        var verdict = await Validate(before, after, Entry());

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
    }

    [Fact]
    public async Task A_line_that_does_not_name_the_property_is_not_explained()
    {
        var props = Props("11.0.0-rc.1").Replace("<LangVersion>preview</LangVersion>", "<OtherVersion>11.0.0-rc.1</OtherVersion>", StringComparison.Ordinal);
        var before = repo.Commit((Project, Csproj), ("Directory.Build.props", props));
        var after = repo.Change(before, ("Directory.Build.props", props.Replace("<OtherVersion>11.0.0-rc.1", "<OtherVersion>11.0.0-rc.2", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Entry());

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task Another_change_in_the_admitted_props_file_is_still_refused()
    {
        var before = repo.Commit((Project, Csproj), ("Directory.Build.props", Props("11.0.0-rc.1")));
        var after = repo.Change(before, ("Directory.Build.props", Props("11.0.0-rc.2").Replace("preview", "latest", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, Entry());

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task An_added_props_file_stays_refused()
    {
        var before = repo.Commit((Project, Csproj));
        var after = repo.Change(before, ("Directory.Build.props", Props("11.0.0-rc.2")));

        var verdict = await Validate(before, after, Entry());

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
    }

    // D4: SpaServices' Demo/Xsrf/ClientApp/package.json:24. No writer change: the line names npm, and '@' is not a
    // version character, so 10.9.2 → 10.9.3 is a planned swap at a token boundary.
    [Fact]
    public async Task A_packageManager_pin_is_a_planned_substitution()
    {
        const string file = "Demo/Xsrf/ClientApp/package.json";
        const string json = "{\n  \"name\": \"xsrf\",\n  \"private\": true,\n  \"packageManager\": \"npm@10.9.2\"\n}\n";
        var before = repo.Commit((file, json));
        var after = repo.Change(before, (file, json.Replace("npm@10.9.2", "npm@10.9.3", StringComparison.Ordinal)));

        var verdict = await Validate(before, after, new DependencyPlanEntry("npm", file, "10.9.2", "10.9.3", "npm", "npm"));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal("npm");
    }
}
