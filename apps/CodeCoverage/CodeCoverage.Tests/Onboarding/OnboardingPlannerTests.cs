using CodeCoverage.Onboarding;
using CodeCoverage.PackageVersioning;
using CodeCoverage.Versioning.Graph;
using Xunit;

namespace CodeCoverage.Tests.Onboarding;

/// <summary>
/// #571 M10b (coverage_backport_PRD D3, §8.4 "M10"): the onboarding chooser's one pull request, planned on the default
/// branch's head for the features the owner picked.
/// </summary>
public class OnboardingPlannerTests
{
    private static InMemoryTree DotnetRepository(string? coverageYml = "gate:\n  patchTarget: 80\n")
    {
        var tree = new InMemoryTree()
            .Add("src/Tools/Tools.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <PackageId>Acme.Tools</PackageId>
                    <Version>11.2.0</Version>
                  </PropertyGroup>
                </Project>

                """.ReplaceLineEndings("\n"));
        return coverageYml is null ? tree : tree.Add("coverage.yml", coverageYml);
    }

    private static OnboardingInputs Inputs(params EOnboardingFeature[] features) => new()
    {
        Features = features.ToHashSet(),
        DefaultBranch = "master",
        Branches = ["master", "release/6.0", "feature/x"],
    };

    private static string CoverageYml(OnboardingPlan plan) => plan.Files.Single(f => f.Path == "coverage.yml").Content!;

    [Fact]
    public async Task Every_feature_lands_in_one_coverage_yml_in_alphabetical_order()
    {
        var plan = await OnboardingPlanner.PlanAsync(DotnetRepository(), Inputs(Enum.GetValues<EOnboardingFeature>()));

        var yml = CoverageYml(plan);
        var order = new[] { "# dependencies:", "\ngate:", "\nmergeQueue:", "\nversioning:" }.Select(k => yml.IndexOf(k, StringComparison.Ordinal)).ToList();
        order.Should().NotContain(-1);
        order.Should().Equal(order.Order().ToList());
        yml.Should().Contain("mergeQueue:\n  enabled: true\n  branches: [master, supported-lines]\n");
        yml.Should().Contain("versioning:\n  lines: {}");

        plan.Files.Select(f => f.Path).Should().Contain(".github/workflows/dependency-lookup.yml");
        plan.Renames.Renames.Select(r => $"{r.From} -> {r.To}").Should().Equal(["release/6.0 -> release/dotnet-6.x"]);
        plan.Seeds.Select(s => $"{s.PackageId}@{s.Version}").Should().Equal(["Acme.Tools@11.2.0"]);
        plan.OnMerge.Should().BeEquivalentTo([EOnboardingFeature.DependencyUpdates, EOnboardingFeature.SizeLabels]);
        plan.Body.Should().Contain("| `release/6.0` | `release/dotnet-6.x` |").And.Contain("/coverage/setup");
    }

    [Fact]
    public async Task Release_lines_bring_versioning_with_them()
    {
        var plan = await OnboardingPlanner.PlanAsync(DotnetRepository(), Inputs(EOnboardingFeature.ReleaseLines));

        plan.Features.Should().BeEquivalentTo([EOnboardingFeature.ReleaseLines, EOnboardingFeature.Versioning]);
        CoverageYml(plan).Should().Contain("versioning:\n  lines: {}");
    }

    private const string PublishWorkflow = "on: push\njobs:\n  pack:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v5\n"
        + "      - name: Version the packages\n        uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1\n"
        + "      - run: dotnet pack\n      - run: dotnet nuget push '*.nupkg' --source https://api.nuget.org/v3/index.json --skip-duplicate\n";

    [Fact]
    public async Task With_one_versioning_workflow_the_current_lines_are_promoted()
    {
        // Segment T6: the default is `/release`, waiting for the one push workflow that runs the versioning step.
        var plan = await OnboardingPlanner.PlanAsync(DotnetRepository().Add(".github/workflows/publish.yml", PublishWorkflow), Inputs(EOnboardingFeature.ReleaseLines));

        plan.PromotesLines.Should().BeTrue();
        CoverageYml(plan).Should().Contain("versioning:\n  lines:").And.Contain("\n    current: promote").And.Contain("\n    trigger: manual\n    workflow: publish.yml");
        VersioningYml.Parse(CoverageYml(plan)).Config!.Lines!.PromotesCurrent.Should().BeTrue();
        plan.Body.Should().Contain("comments `/release`");
        plan.Notes.Should().NotContain(n => n.Contains("take backports", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, "No push workflow runs the versioning step")]
    [InlineData(2, "`deploy.yml`, `publish.yml`")]
    public async Task Without_exactly_one_versioning_workflow_the_lines_take_backports_and_a_note_says_how_to_switch(int workflows, string note)
    {
        var tree = DotnetRepository();
        if (workflows > 0) tree.Add(".github/workflows/publish.yml", PublishWorkflow);
        if (workflows > 1) tree.Add(".github/workflows/deploy.yml", PublishWorkflow);

        var plan = await OnboardingPlanner.PlanAsync(tree, Inputs(EOnboardingFeature.ReleaseLines));

        plan.PromotesLines.Should().BeFalse();
        CoverageYml(plan).Should().Contain("versioning:\n  lines: {}");
        plan.Notes.Should().Contain(n => n.Contains(note, StringComparison.Ordinal) && n.Contains("current: promote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Existing_lines_that_could_promote_are_left_alone_and_a_note_says_how_to_switch()
    {
        // Gap G2 (SparkTest trial): its coverage.yml had `lines: {}` already, so T6 never fired and nothing said so.
        var tree = DotnetRepository("versioning:\n  mode: server\n  lines: {}\n").Add(".github/workflows/publish.yml", PublishWorkflow);

        var plan = await OnboardingPlanner.PlanAsync(tree, Inputs(EOnboardingFeature.ReleaseLines));

        plan.PromotesLines.Should().BeFalse();
        plan.Files.Should().NotContain(f => f.Path == "coverage.yml");
        plan.Notes.Should().Contain(n => n.StartsWith("coverage.yml has `versioning.lines` already", StringComparison.Ordinal)
            && n.Contains("`{ current: promote, trigger: manual, workflow: publish.yml }`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Planning_again_on_the_result_changes_nothing()
    {
        var tree = DotnetRepository();
        var first = await OnboardingPlanner.PlanAsync(tree, Inputs(Enum.GetValues<EOnboardingFeature>()) with { IsTagged = (_, _) => Task.FromResult(false) });
        foreach (var file in first.Files)
            tree.Add(file.Path, file.Content!);

        var second = await OnboardingPlanner.PlanAsync(tree, Inputs(Enum.GetValues<EOnboardingFeature>()) with { IsTagged = (_, _) => Task.FromResult(true) });

        second.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task A_flow_style_versioning_section_gets_a_note_instead_of_an_edit()
    {
        var plan = await OnboardingPlanner.PlanAsync(DotnetRepository("versioning: { mode: server }\n"), Inputs(EOnboardingFeature.ReleaseLines));

        plan.Notes.Should().Contain(n => n.Contains("versioning.lines", StringComparison.Ordinal));
        plan.Files.Where(f => f.Path == "coverage.yml").Should().BeEmpty();
    }

    [Fact]
    public async Task Coverage_alone_writes_nothing_and_asks_for_the_upload_step()
    {
        var plan = await OnboardingPlanner.PlanAsync(DotnetRepository(), Inputs(EOnboardingFeature.Coverage));

        plan.Files.Should().BeEmpty();
        plan.Renames.Renames.Should().BeEmpty();
        plan.Body.Should().Contain("- [ ]").And.Contain("/coverage/setup");
    }
}
