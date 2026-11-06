using System.Net;
using System.Net.Http.Json;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #571 M10c (coverage_backport_PRD D3): the onboarding chooser's one commit, force-pushed to <c>mintplayer/onboarding</c>. It
/// writes what the versioning onboarding may write plus the dependency onboarding's closed set, and deletes only Dependabot's file.
/// </summary>
public class OnboardingWriterJobTests : WriterTestBase
{
    private (Origin Origin, string Base) Seeded()
    {
        var origin = Seed();
        origin.Checkout("main").Write("coverage.yml", "gate:\n  patchTarget: 80\n").Write(".github/dependabot.yml", "version: 2\n");
        var baseSha = origin.Commit("coverage.yml and dependabot");
        origin.Push("main");
        return (origin, baseSha);
    }

    private Dictionary<string, object?> Job(Origin origin, string expectedSha, params object[] files) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["baseBranch"] = "main",
        ["expectedSha"] = expectedSha,
        ["author"] = new { name = "CodeCoverage", email = "bot@example.invalid", date = CommitterDate },
        ["committer"] = new { name = "CodeCoverage", email = "bot@example.invalid", date = CommitterDate },
        ["message"] = "Set up MintPlayer CodeCoverage\n",
        ["files"] = files,
    };

    [Fact]
    public async Task D3_one_commit_on_the_onboarding_branch_writes_both_sets_and_deletes_dependabot()
    {
        var (origin, baseSha) = Seeded();

        var response = await Writer.Client.PostAsJsonAsync("/jobs/onboarding", Job(origin, baseSha,
            new { path = "coverage.yml", content = "gate:\n  patchTarget: 80\n\nversioning:\n  lines: {}\n" },
            new { path = ".github/workflows/dependency-lookup.yml", content = "name: lookup\n" },
            new { path = ".github/workflows/regenerate.yml", content = "name: regenerate\n" },
            new { path = "Directory.Build.props", content = "<Project />\n" },
            new { path = ".github/dependabot.yml", delete = true }), GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        var answer = (await response.Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!;

        answer.Outcome.Should().Be("pushed", answer.Detail);
        DependencyBranches.FeatureOnboarding.Should().Be("mintplayer/onboarding");
        origin.Ref($"refs/heads/{DependencyBranches.FeatureOnboarding}").Should().Be(answer.Sha);
        origin.InBare("rev-parse", $"{answer.Sha}^").Trim().Should().Be(baseSha);
        origin.InBare("diff-tree", "-r", "--name-status", "--no-commit-id", baseSha, answer.Sha!).ReplaceLineEndings("\n").Trim().Should().Be(
            "D\t.github/dependabot.yml\nA\t.github/workflows/dependency-lookup.yml\nA\t.github/workflows/regenerate.yml\nA\tDirectory.Build.props\nM\tcoverage.yml");
    }

    [Theory]
    [InlineData(".github/CODEOWNERS", false)]
    [InlineData("coverage.yml", true)]
    [InlineData(".github/workflows/regenerate.yml", true)]
    [InlineData(".gitattributes", false)]
    public async Task D3_a_path_outside_the_onboarding_sets_or_a_delete_other_than_dependabot_is_refused(string path, bool delete)
    {
        var (origin, baseSha) = Seeded();
        object file = delete ? new { path, delete = true } : new { path, content = "x\n" };

        var response = await Writer.Client.PostAsJsonAsync("/jobs/onboarding", Job(origin, baseSha, file), GitWriterHost.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        origin.Ref($"refs/heads/{DependencyBranches.FeatureOnboarding}").Should().BeNull();
    }
}
