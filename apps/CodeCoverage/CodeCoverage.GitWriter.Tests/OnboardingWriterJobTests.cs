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

    [Fact]
    public async Task D9_a_line_job_adds_a_workflow_on_the_line_s_own_onboarding_branch()
    {
        var (origin, baseSha) = Seeded();
        origin.SetBranch("release/dotnet-6.x", baseSha);
        var job = Job(origin, baseSha, new { path = ".github/workflows/regenerate.yml", content = "name: regenerate\n" });
        job["baseBranch"] = "release/dotnet-6.x";
        job["line"] = "dotnet-6.x";

        var response = await Writer.Client.PostAsJsonAsync("/jobs/onboarding", job, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        var answer = (await response.Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!;

        answer.Outcome.Should().Be("pushed", answer.Detail);
        DependencyBranches.FeatureOnboardingOf("dotnet-6.x").Should().Be("mintplayer/onboarding-dotnet-6.x");
        origin.Ref("refs/heads/mintplayer/onboarding-dotnet-6.x").Should().Be(answer.Sha);
        origin.Ref($"refs/heads/{DependencyBranches.FeatureOnboarding}").Should().BeNull();
        origin.InBare("rev-parse", $"{answer.Sha}^").Trim().Should().Be(baseSha);
    }

    [Theory]
    [InlineData("coverage.yml")]
    [InlineData("Directory.Build.props")]
    public async Task D9_a_line_job_writes_workflows_only(string path)
    {
        var (origin, baseSha) = Seeded();
        origin.SetBranch("release/dotnet-6.x", baseSha);
        var job = Job(origin, baseSha, new { path, content = "x\n" });
        job["baseBranch"] = "release/dotnet-6.x";
        job["line"] = "dotnet-6.x";

        var response = await Writer.Client.PostAsJsonAsync("/jobs/onboarding", job, GitWriterHost.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task D15_a_narrowing_job_writes_project_files_and_global_json_on_the_line_s_narrowing_branch()
    {
        var (origin, baseSha) = Seeded();
        origin.SetBranch("release/dotnet-10.x", baseSha);
        var job = Job(origin, baseSha,
            new { path = "src/Hsts/Hsts.csproj", content = "<Project><PropertyGroup><TargetFrameworks>net10.0</TargetFrameworks></PropertyGroup></Project>\n" },
            new { path = "global.json", content = "{ \"sdk\": { \"version\": \"10.0.203\" } }\n" });
        job["baseBranch"] = "release/dotnet-10.x";
        job["line"] = "dotnet-10.x";
        job["narrow"] = true;

        var response = await Writer.Client.PostAsJsonAsync("/jobs/onboarding", job, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        var answer = (await response.Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!;

        answer.Outcome.Should().Be("pushed", answer.Detail);
        DependencyBranches.NarrowingOf("dotnet-10.x").Should().Be("mintplayer/narrow-dotnet-10.x");
        origin.Ref("refs/heads/mintplayer/narrow-dotnet-10.x").Should().Be(answer.Sha);
        origin.Ref("refs/heads/mintplayer/onboarding-dotnet-10.x").Should().BeNull();
        origin.InBare("rev-parse", $"{answer.Sha}^").Trim().Should().Be(baseSha);
    }

    /// <summary>#649: #648 taught the narrowing the workflows' setup-dotnet pins, and this endpoint refused every such narrowing.</summary>
    [Fact]
    public async Task Issue649_a_narrowing_job_writes_a_workflow_beside_global_json()
    {
        var (origin, baseSha) = Seeded();
        origin.SetBranch("release/dotnet-10.x", baseSha);
        var job = Job(origin, baseSha,
            new { path = "global.json", content = "{ \"sdk\": { \"version\": \"10.0.203\" }, \"tools\": { \"dotnet\": \"10.0.203\" } }\n" },
            new { path = ".github/workflows/pull-request.yml", content = "jobs:\n  build:\n    steps:\n    - uses: actions/setup-dotnet@v6\n      with:\n        dotnet-version: 10.0.203\n" });
        job["baseBranch"] = "release/dotnet-10.x";
        job["line"] = "dotnet-10.x";
        job["narrow"] = true;

        var response = await Writer.Client.PostAsJsonAsync("/jobs/onboarding", job, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        var answer = (await response.Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!;

        answer.Outcome.Should().Be("pushed", answer.Detail);
        origin.InBare("diff-tree", "-r", "--name-status", "--no-commit-id", baseSha, answer.Sha!).ReplaceLineEndings("\n").Trim().Should().Be(
            "A\t.github/workflows/pull-request.yml\nA\tglobal.json");
    }

    /// <summary>#649 D10 (S8): a rerun of Set up features plans the same narrowing on the same line head, dated now; the
    /// branch keeps its commit instead of a force-push that restarts the pull request's checks.</summary>
    [Fact]
    public async Task Issue649_a_rerun_with_the_same_files_on_the_same_head_keeps_the_branch_s_commit()
    {
        var (origin, baseSha) = Seeded();
        origin.SetBranch("release/dotnet-10.x", baseSha);
        Dictionary<string, object?> Narrowing(DateTimeOffset date)
        {
            var job = Job(origin, baseSha, new { path = "global.json", content = "{ \"sdk\": { \"version\": \"10.0.203\" } }\n" });
            job["baseBranch"] = "release/dotnet-10.x";
            job["line"] = "dotnet-10.x";
            job["narrow"] = true;
            job["author"] = job["committer"] = new { name = "CodeCoverage", email = "bot@example.invalid", date };
            return job;
        }

        var first = (await (await Writer.Client.PostAsJsonAsync("/jobs/onboarding", Narrowing(CommitterDate), GitWriterHost.Json))
            .Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!;
        var second = (await (await Writer.Client.PostAsJsonAsync("/jobs/onboarding", Narrowing(CommitterDate.AddDays(1)), GitWriterHost.Json))
            .Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!;

        first.Outcome.Should().Be("pushed", first.Detail);
        second.Outcome.Should().Be("pushed", second.Detail);
        second.Sha.Should().Be(first.Sha);
        origin.Ref("refs/heads/mintplayer/narrow-dotnet-10.x").Should().Be(first.Sha);
    }

    [Theory]
    [InlineData(".github/workflows/nested/regenerate.yml", "dotnet-10.x")]   // GitHub runs no workflow from a subdirectory
    [InlineData(".github/workflows/README.md", "dotnet-10.x")]
    [InlineData("coverage.yml", "dotnet-10.x")]
    [InlineData("src/Hsts/Hsts.csproj", null)]   // narrowing is a line's only
    public async Task D15_a_narrowing_job_writes_msbuild_files_global_json_and_workflows_only(string path, string? line)
    {
        var (origin, baseSha) = Seeded();
        origin.SetBranch("release/dotnet-10.x", baseSha);
        var job = Job(origin, baseSha, new { path, content = "x\n" });
        job["baseBranch"] = "release/dotnet-10.x";
        job["line"] = line;
        job["narrow"] = true;

        var response = await Writer.Client.PostAsJsonAsync("/jobs/onboarding", job, GitWriterHost.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
