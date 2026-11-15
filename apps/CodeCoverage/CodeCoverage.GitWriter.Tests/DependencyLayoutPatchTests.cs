using CodeCoverage.Git.Dependencies;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// multi-target-dependency-split-PRD P5 (D-e, S4): a plan item with a layout moves a package into one canonical ItemGroup per
/// TFM, which adds and removes lines, so the line rule cannot admit it. The writer checks such a file as XML instead: with
/// the planned package taken out of both sides, the file before and after must be the same XML, and every new occurrence
/// must be the old element with only its version changed, under <c>'$(TargetFramework)' == 'X'</c>, one per planned TFM at
/// its planned version. Real <c>git diff</c> on a real repository.
/// </summary>
public class DependencyLayoutPatchTests : IDisposable
{
    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private const string Project = "FolderHasher/FolderHasher.csproj";
    private const string Group = "platform:dotnet 11.0";
    private const string Fsg = "Microsoft.Extensions.FileSystemGlobbing";

    private static string Csproj(string tfms, string items) => "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
        + $"  <PropertyGroup>\n    <TargetFrameworks>{tfms}</TargetFrameworks>\n  </PropertyGroup>\n\n{items}</Project>\n";

    private const string Before = "  <ItemGroup>\n"
        + "    <PackageReference Include=\"Microsoft.Extensions.FileSystemGlobbing\" Version=\"10.0.5\" />\n"
        + "    <PackageReference Include=\"Other\" Version=\"1.0.0\" />\n"
        + "  </ItemGroup>\n\n";

    private static string Split(string net10 = "10.0.15", string net11 = "11.0.0", string extra = "", string condition = "'$(TargetFramework)' == 'net10.0'")
        => "  <ItemGroup>\n    <PackageReference Include=\"Other\" Version=\"1.0.0\" />\n  </ItemGroup>\n\n"
           + $"  <ItemGroup Condition=\"{condition}\">\n    <PackageReference Include=\"Microsoft.Extensions.FileSystemGlobbing\" Version=\"{net10}\" />\n{extra}  </ItemGroup>\n\n"
           + $"  <ItemGroup Condition=\"'$(TargetFramework)' == 'net11.0'\">\n    <PackageReference Include=\"Microsoft.Extensions.FileSystemGlobbing\" Version=\"{net11}\" />\n  </ItemGroup>\n\n";

    private static DependencyPlanEntry Entry(params (string Tfm, string Version)[] layout)
        => new(Fsg, Project, "10.0.5", layout.Max(l => l.Version)!, Group, "nuget")
        {
            Layout = [.. layout.Select(l => new PlanTfmVersion { TargetFramework = l.Tfm, Version = l.Version })],
        };

    private static readonly DependencyPlanEntry Planned = Entry(("net10.0", "10.0.15"), ("net11.0", "11.0.0"));

    private async Task<DependencyPatchVerdict> Validate(string before, string after, DependencyPatchRequest request)
        => await DependencyPatchValidator.ValidateAsync(repo.Diff(before, after), request, repo.Tree(before));

    private async Task<DependencyPatchVerdict> Validate(string before, string after, params DependencyPlanEntry[] entries)
        => await Validate(before, after, new DependencyPatchRequest(entries));

    [Fact]
    public async Task A_split_into_canonical_groups_is_admitted()
    {
        var before = repo.Commit((Project, Csproj("net10.0;net11.0", Before)));
        var after = repo.Change(before, (Project, Csproj("net10.0;net11.0", Split())));

        var verdict = await Validate(before, after, Planned);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal(Group);
    }

    [Fact]
    public async Task An_element_smuggled_into_a_new_group_is_refused()
    {
        var before = repo.Commit((Project, Csproj("net10.0;net11.0", Before)));
        var after = repo.Change(before, (Project, Csproj("net10.0;net11.0", Split(extra: "    <PackageReference Include=\"Evil\" Version=\"6.6.6\" />\n"))));

        var verdict = await Validate(before, after, Planned);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_version_other_than_the_planned_one_is_refused()
    {
        var before = repo.Commit((Project, Csproj("net10.0;net11.0", Before)));
        var after = repo.Change(before, (Project, Csproj("net10.0;net11.0", Split(net10: "10.0.14"))));

        var verdict = await Validate(before, after, Planned);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Detail.Should().Contain("10.0.14");
    }

    [Theory]
    [InlineData("'$(TargetFramework)'=='net10.0'")]
    [InlineData("'$(Configuration)' == 'Debug'")]
    public async Task A_condition_the_bot_does_not_write_is_refused(string condition)
    {
        var before = repo.Commit((Project, Csproj("net10.0;net11.0", Before)));
        var after = repo.Change(before, (Project, Csproj("net10.0;net11.0", Split(condition: condition))));

        var verdict = await Validate(before, after, Planned);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task An_attribute_changed_on_the_moved_element_is_refused()
    {
        var before = repo.Commit((Project, Csproj("net10.0;net11.0", Before)));
        var after = repo.Change(before, (Project, Csproj("net10.0;net11.0",
            Split().Replace("Version=\"11.0.0\" />", "Version=\"11.0.0\" PrivateAssets=\"all\" />", StringComparison.Ordinal))));

        var verdict = await Validate(before, after, Planned);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_TFM_missing_from_the_layout_is_refused()
    {
        var before = repo.Commit((Project, Csproj("net10.0;net11.0", Before)));
        var after = repo.Change(before, (Project, Csproj("net10.0;net11.0", Split())));

        var verdict = await Validate(before, after, Entry(("net10.0", "10.0.15"), ("net11.0", "11.0.0"), ("net12.0", "12.0.0")));

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Detail.Should().Contain("net12.0");
    }

    /// <summary>P3: the TFM list the plan's platform move names for the file is admitted with the split, and applies the move.</summary>
    [Fact]
    public async Task The_planned_TFM_list_is_admitted_with_the_layout()
    {
        const string group = "platform:dotnet 12.0";
        var before = repo.Commit((Project, Csproj("net10.0;net11.0", Before)));
        var net12 = "  <ItemGroup Condition=\"'$(TargetFramework)' == 'net12.0'\">\n    <PackageReference Include=\"Microsoft.Extensions.FileSystemGlobbing\" Version=\"12.0.0\" />\n  </ItemGroup>\n\n";
        var after = repo.Change(before, (Project, Csproj("net10.0;net11.0;net12.0", Split() + net12)));
        var move = new PlanPlatform
        {
            Sdk = "dotnet",
            FromChannel = "11.0",
            ToChannel = "12.0",
            TargetFrameworks = [new PlanTargetFrameworks { File = Project, Value = "net10.0;net11.0;net12.0" }],
        };
        var entry = Entry(("net10.0", "10.0.15"), ("net11.0", "11.0.0"), ("net12.0", "12.0.0")) with { GroupName = group };
        var request = new DependencyPatchRequest([entry])
        {
            Platforms = [new DependencyPlatformMove(group, move, [new DependencyPlatformLocation("dotnet", Project, "tfm", "net10.0;net11.0")])],
            ClaimedAppliedGroups = [group],
        };

        var verdict = await Validate(before, after, request);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted, verdict.Detail);
        verdict.AppliedGroups.Should().Equal(group);

        var other = repo.Change(before, (Project, Csproj("net10.0;net11.0;net13.0", Split() + net12)));
        (await Validate(before, other, request)).Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }
}
