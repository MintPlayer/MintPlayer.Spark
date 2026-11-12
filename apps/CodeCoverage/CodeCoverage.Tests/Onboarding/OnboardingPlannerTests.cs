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

    private static InMemoryTree MultiTargetRepository(string targets, string? coverageYml = "gate:\n  patchTarget: 80\n")
    {
        var tree = new InMemoryTree()
            .Add("src/Tools/Tools.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFrameworks>{targets}</TargetFrameworks>\n"
                + "    <PackageId>Acme.Tools</PackageId>\n    <Version>11.2.0</Version>\n  </PropertyGroup>\n</Project>\n")
            .Add("src/Other/Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n"
                + "    <PackageId>Acme.Other</PackageId>\n    <Version>11.0.0</Version>\n  </PropertyGroup>\n</Project>\n");
        return coverageYml is null ? tree : tree.Add("coverage.yml", coverageYml);
    }

    /// <summary>
    /// docs/onboarding_line_cuts_PRD.md D1, D2 (the Tools onboarding, 2026-11-12): the current line of each anchored major
    /// with no branch is created; a lower target framework a package still has is only offered, and becomes a cut once chosen.
    /// </summary>
    [Fact]
    public async Task LineCuts_the_current_line_is_created_and_lower_targets_are_offered_until_chosen()
    {
        var plan = await OnboardingPlanner.PlanAsync(MultiTargetRepository("netstandard2.0;net9.0;net10.0;net11.0"), Inputs(EOnboardingFeature.ReleaseLines));

        plan.Cuts.Select(c => (c.Branch, c.TargetFramework, string.Join(",", c.Packages))).Should().Equal([("release/dotnet-11.x", (string?)null, "Acme.Other,Acme.Tools")]);
        plan.OfferedLines.Select(c => (c.Branch, c.TargetFramework)).Should().Equal([("release/dotnet-10.x", "net10.0"), ("release/dotnet-9.x", "net9.0")],
            "netstandard names no major, and offers sort by branch");
        plan.Body.Should().Contain("- `release/dotnet-11.x` (the current line): `Acme.Other`, `Acme.Tools`.")
            .And.Contain("Not created, as chosen").And.Contain("- `release/dotnet-10.x`: still targets net10.0.")
            .And.Contain("- 1 release line(s) are created from the merge commit");

        var chosen = await OnboardingPlanner.PlanAsync(MultiTargetRepository("netstandard2.0;net9.0;net10.0;net11.0"),
            Inputs(EOnboardingFeature.ReleaseLines) with { OlderLines = ["release/dotnet-10.x", "release/dotnet-8.x"] });
        chosen.Cuts.Select(c => c.Branch).Should().Equal(["release/dotnet-11.x", "release/dotnet-10.x"], "a chosen line no package offers is ignored");
        chosen.Body.Should().Contain("- `release/dotnet-10.x` (still targets net10.0): `Acme.Tools`.").And.Contain("- `release/dotnet-9.x`: still targets net9.0.");

        // #638 D15: only a chosen older line is narrowed afterwards, and the plan says so.
        const string Narrowing = "the bot opens a pull request into it that drops the higher target frameworks";
        plan.Body.Should().NotContain(Narrowing);
        chosen.Body.Should().Contain(Narrowing).And.Contain("`#if NETxx_OR_GREATER`");
    }

    [Fact]
    public async Task LineCuts_a_line_that_exists_or_that_a_rename_produces_is_not_created_and_an_anchored_package_is_left_to_the_run()
    {
        var existing = await OnboardingPlanner.PlanAsync(MultiTargetRepository("net6.0;net10.0;net11.0"),
            Inputs(EOnboardingFeature.ReleaseLines) with { Branches = ["master", "release/dotnet-11.x", "release/6.0"] });
        existing.Renames.Renames.Select(r => r.To).Should().Equal(["release/dotnet-6.x"]);
        existing.Cuts.Should().BeEmpty();
        existing.OfferedLines.Select(c => c.Branch).Should().Equal(["release/dotnet-10.x"], "6.x is what the rename produces");

        var anchored = await OnboardingPlanner.PlanAsync(MultiTargetRepository("net10.0;net11.0", "versioning:\n  lines: {}\n  packages:\n    Acme.Tools:\n      anchor: free\n"),
            Inputs(EOnboardingFeature.ReleaseLines));
        anchored.Cuts.Select(c => string.Join(",", c.Packages)).Should().Equal(["Acme.Other"]);
        anchored.OfferedLines.Should().BeEmpty();
    }

    [Fact]
    public async Task LineCuts_without_release_lines_nothing_is_created()
    {
        var plan = await OnboardingPlanner.PlanAsync(MultiTargetRepository("net10.0;net11.0"), Inputs(EOnboardingFeature.Versioning));

        (plan.Cuts.Count, plan.OfferedLines.Count).Should().Be((0, 0));
    }

    private const string PublishWorkflow ="on: push\njobs:\n  pack:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v5\n"
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

    // The Tools onboarding (2026-11-12): its publish workflow ran on master only, and its pull-request workflow only for
    // pull requests into master, so with lines on nothing would ever have published.
    private const string MasterPublishWorkflow = "name: publish-release\n\non:\n  push:\n    branches:\n      - 'master'\n      # - 'release/**'\n\n"
        + "jobs:\n  pack:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v5\n"
        + "      - name: Version the packages\n        id: versioning\n        uses: MintPlayer/MintPlayer.Spark/apps/CodeCoverage/versioning-action@versioning-v1\n"
        + "      - run: dotnet pack\n      - run: dotnet nuget push '*.nupkg' --source https://api.nuget.org/v3/index.json --skip-duplicate\n";

    private const string MasterPullRequestWorkflow = "name: pull-request\n\non:\n  pull_request:\n    branches: [ master ]\n\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test\n";

    private const string MasterDeployWorkflow = "on:\n  push:\n    branches: [master]\njobs:\n  deploy:\n    runs-on: ubuntu-latest\n    steps:\n      - run: ./deploy.sh\n";

    private static string Planned(OnboardingPlan plan, string path) => plan.Files.Single(f => f.Path == path).Content!;

    [Fact]
    public async Task Release_lines_add_the_lines_to_the_publish_push_and_the_pull_request_filters_but_not_to_a_deploy()
    {
        var tree = DotnetRepository()
            .Add(".github/workflows/publish-release.yml", MasterPublishWorkflow)
            .Add(".github/workflows/pull-request.yml", MasterPullRequestWorkflow)
            .Add(".github/workflows/deploy.yml", MasterDeployWorkflow);

        var plan = await OnboardingPlanner.PlanAsync(tree, Inputs(EOnboardingFeature.ReleaseLines));

        Planned(plan, ".github/workflows/publish-release.yml").Should().Contain("    branches:\n      - 'master'\n      - 'release/*-*.x'\n      # - 'release/**'\n");
        Planned(plan, ".github/workflows/pull-request.yml").Should().Contain("    branches: [ master, 'release/*-*.x' ]\n");
        plan.Files.Should().NotContain(f => f.Path == ".github/workflows/deploy.yml");
        plan.Body.Should().Contain("These workflows now run on the lines too").And.Contain("`.github/workflows/publish-release.yml`, `.github/workflows/pull-request.yml`");

        foreach (var file in plan.Files)
            tree.Add(file.Path, file.Content!);
        var again = await OnboardingPlanner.PlanAsync(tree, Inputs(EOnboardingFeature.ReleaseLines) with { IsTagged = (_, _) => Task.FromResult(true) });
        again.Files.Should().BeEmpty();
    }

    [Theory]
    [InlineData("on:\n  push:\n    branches: ['master', 'release/**']\n")]
    [InlineData("on:\n  push:\n    tags: ['v*']\n")]
    [InlineData("on: [push, pull_request]\n")]
    [InlineData("on:\n  pull_request:\n    branches: [develop]\n  push:\n    branches: ['**']\n")]
    public async Task A_filter_that_already_runs_on_the_lines_or_does_not_name_the_default_branch_is_left_alone(string on)
    {
        var workflow = on + MasterPublishWorkflow[MasterPublishWorkflow.IndexOf("jobs:", StringComparison.Ordinal)..];
        var tree = DotnetRepository().Add(".github/workflows/publish-release.yml", workflow);

        var plan = await OnboardingPlanner.PlanAsync(tree, Inputs(EOnboardingFeature.ReleaseLines));

        plan.Files.Should().NotContain(f => f.Path == ".github/workflows/publish-release.yml" && !f.Content!.Contains(on, StringComparison.Ordinal));
        plan.Body.Should().NotContain("These workflows now run on the lines too");
    }

    [Theory]
    [InlineData("on:\n  pull_request:\n    branches-ignore: ['release/**']\n", "ignores the release lines in its `pull_request` trigger")]
    [InlineData("on:\n  pull_request:\n    branches: [master, '!master-old']\n", "has a negated branch in its `pull_request` trigger")]
    public async Task A_filter_the_bot_cannot_extend_gets_a_note(string on, string note)
    {
        var tree = DotnetRepository().Add(".github/workflows/ci.yml", on + "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet test\n");

        var plan = await OnboardingPlanner.PlanAsync(tree, Inputs(EOnboardingFeature.ReleaseLines));

        plan.Files.Should().NotContain(f => f.Path == ".github/workflows/ci.yml");
        plan.Notes.Should().Contain(n => n.StartsWith("`.github/workflows/ci.yml` ", StringComparison.Ordinal) && n.Contains(note, StringComparison.Ordinal)
            && n.Contains("`release/*-*.x`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_block_list_with_crlf_line_endings_keeps_them()
    {
        var tree = DotnetRepository().Add(".github/workflows/pull-request.yml",
            "on:\r\n  pull_request:\r\n    branches:\r\n    - master\r\njobs:\r\n  build:\r\n    runs-on: ubuntu-latest\r\n    steps:\r\n    - run: dotnet test");

        var plan = await OnboardingPlanner.PlanAsync(tree, Inputs(EOnboardingFeature.ReleaseLines));

        Planned(plan, ".github/workflows/pull-request.yml").Should().Contain("    branches:\r\n    - master\r\n    - 'release/*-*.x'\r\njobs:");
    }

    [Fact]
    public async Task Without_release_lines_no_trigger_changes()
    {
        var tree = DotnetRepository("versioning: { mode: server }\n").Add(".github/workflows/pull-request.yml", MasterPullRequestWorkflow);

        var plan = await OnboardingPlanner.PlanAsync(tree, Inputs(EOnboardingFeature.ReleaseLines));

        plan.Files.Should().NotContain(f => f.Path == ".github/workflows/pull-request.yml");
    }

    [Fact]
    public async Task A_merge_queue_section_without_branches_gets_the_lines()
    {
        var plan = await OnboardingPlanner.PlanAsync(DotnetRepository("mergeQueue:\n  enabled: true\n"), Inputs(EOnboardingFeature.ReleaseLines, EOnboardingFeature.MergeQueue));

        CoverageYml(plan).Should().StartWith("mergeQueue:\n  branches: [master, supported-lines]\n  enabled: true\n");
        plan.Notes.Should().NotContain(n => n.Contains("supported-lines", StringComparison.Ordinal));
        plan.Body.Should().Contain("it now queues the supported release lines too");
    }

    [Fact]
    public async Task A_merge_queue_section_with_its_own_branches_is_left_to_the_owner()
    {
        var plan = await OnboardingPlanner.PlanAsync(DotnetRepository("mergeQueue:\n  enabled: true\n  branches: [master, develop]\n"),
            Inputs(EOnboardingFeature.ReleaseLines, EOnboardingFeature.MergeQueue));

        CoverageYml(plan).Should().Contain("  branches: [master, develop]\n");
        plan.Notes.Should().Contain(n => n.Contains("add `supported-lines` to `mergeQueue.branches`", StringComparison.Ordinal));
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
