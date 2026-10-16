using System.Text;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// The <c>migrate</c> patch profile (docs/code-coverage/dependency-updates-PRD.md R9, R18; §13.11.3 #18): a deny-list
/// only. The real S17 migrations (Fixtures/Migrate with SOURCE.md: Angular 21→22 core and CLI, Nx 22→23, and the two
/// partial runs, whose detection is the completion marker's job, not the profile's) all pass; <c>.github/</c>,
/// <c>coverage.yml</c>, the plan path, symlinks, mode changes and executables are refused.
/// </summary>
public class MigratePatchProfileTests : IDisposable
{
    private const long Cap = 8L * 1024 * 1024;

    private readonly BaseRepo repo = new();

    public void Dispose() => repo.Dispose();

    [Theory]
    [InlineData("angular-core-21-to-22.patch")]
    [InlineData("angular-cli-karma-21-to-22.patch")]
    [InlineData("angular-core-partial-EPERM.patch")]
    [InlineData("nx-22-to-23.patch")]
    [InlineData("nx-partial-EPERM.patch")]
    public void Every_real_migration_patch_passes(string name)
    {
        var verdict = MigratePatchValidator.Validate(File.ReadAllBytes(BaseRepo.Fixture("Migrate", name)), Cap);

        verdict.Detail.Should().BeNull();
        verdict.Accepted.Should().BeTrue();
    }

    [Fact]
    public void The_nx_migration_adds_a_file_and_edits_build_configuration_which_R18_allows()
    {
        var verdict = MigratePatchValidator.Validate(File.ReadAllBytes(BaseRepo.Fixture("Migrate", "nx-22-to-23.patch")), Cap);

        verdict.Paths.Should().Contain("migrations.json");
        verdict.Paths.Should().Contain("nx.json");
        verdict.Paths.Should().Contain("package.json");
        verdict.Paths.Should().Contain("package-lock.json");
    }

    private PatchVerdict Migrate(params (string Path, string? Content)[] changes)
    {
        var before = repo.Commit(("src/app.ts", "export const a = 1;\n"), (".github/workflows/ci.yml", "on: push\n"), ("coverage.yml", "mergeQueue: {}\n"));
        var after = repo.Change(before, changes);
        return MigratePatchValidator.Validate(repo.Diff(before, after), Cap);
    }

    [Fact]
    public void A_source_edit_with_a_new_file_passes()
    {
        var verdict = Migrate(("src/app.ts", "export const a = 2;\n"), ("src/new.ts", "export {};\n"), ("tsconfig.json", "{}\n"));

        verdict.Accepted.Should().BeTrue();
    }

    // §13.11.3 #18.
    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/new.yml")]
    [InlineData(".GitHub/actions/x/action.yml")]
    [InlineData(".gitattributes")]
    public void A_migration_touching_github_or_git_attributes_is_refused(string path)
    {
        var verdict = Migrate(("src/app.ts", "export const a = 2;\n"), (path, "on: pull_request_target\n"));

        verdict.Reason.Should().Be(PatchReasons.UnsafePath);
    }

    [Fact]
    public void A_migration_touching_coverage_yml_is_refused()
    {
        var verdict = Migrate(("coverage.yml", "mergeQueue: { enabled: true }\n"));

        verdict.Reason.Should().Be(DependencyPatchReasons.DeniedPath);
    }

    [Fact]
    public void A_migration_touching_the_plan_path_is_refused()
    {
        var verdict = Migrate((DependencyFiles.PlanPath, "{}\n"));

        verdict.Reason.Should().Be(DependencyPatchReasons.PlanPath);
    }

    [Theory]
    [InlineData("120000", PatchReasons.Symlink)]
    [InlineData("100755", PatchReasons.UnsupportedMode)]
    public void A_migration_adding_a_symlink_or_an_executable_is_refused(string mode, string reason)
    {
        var before = repo.Commit(("src/app.ts", "export const a = 1;\n"));
        var after = repo.Commit([("src/app.ts", "100644", Encoding.UTF8.GetBytes("export const a = 1;\n")), ("tools/run", mode, Encoding.UTF8.GetBytes("../../etc/passwd"))]);

        var verdict = MigratePatchValidator.Validate(repo.Diff(before, after), Cap);

        verdict.Reason.Should().Be(reason);
    }

    [Fact]
    public void A_migration_changing_a_mode_is_refused()
    {
        var before = repo.Commit(("tools/run.sh", "echo hi\n"));
        var after = repo.Commit([("tools/run.sh", "100755", Encoding.UTF8.GetBytes("echo hi\n"))]);

        var verdict = MigratePatchValidator.Validate(repo.Diff(before, after), Cap);

        verdict.Reason.Should().Be(PatchReasons.ModeChange);
    }
}
