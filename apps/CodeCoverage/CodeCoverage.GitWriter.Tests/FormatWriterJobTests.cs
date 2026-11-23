using System.Net;
using System.Net.Http.Json;
using System.Text;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Jobs.Dependencies;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// #657 M5 (docs/coverage_file_formatter_PRD.md D7, D11, D15): a FormatRepository or LintRepository run's patch, applied on
/// the default branch's commit and force-pushed to <c>mintplayer/format</c> or <c>mintplayer/lint</c>. The patch may edit
/// any writable file, workflows included, never an excluded one, and never adds, deletes or renames a file.
/// </summary>
public class FormatWriterJobTests : WriterTestBase
{
    private const string Unformatted = "class A{}\n";
    private const string Formatted = "class A { }\n";

    private (Origin Origin, string Base) Seeded()
    {
        var origin = Seed();
        origin.Checkout("main").Write("src/A.cs", Unformatted).Write("src/A.g.cs", Unformatted).Write(".github/workflows/ci.yml", "on:  push\n");
        var baseSha = origin.Commit("unformatted");
        origin.Push("main");
        return (origin, baseSha);
    }

    private static string Edit(string path, string before, string after) => $"""
        diff --git a/{path} b/{path}
        index 1111111..2222222 100644
        --- a/{path}
        +++ b/{path}
        @@ -1 +1 @@
        -{before.TrimEnd('\n')}
        +{after.TrimEnd('\n')}

        """.ReplaceLineEndings("\n");

    private Dictionary<string, object?> Job(Origin origin, string expectedSha, string mode, string patch, params string[] exclude) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["baseBranch"] = "main",
        ["expectedSha"] = expectedSha,
        ["author"] = new { name = "CodeCoverage", email = "bot@example.invalid", date = CommitterDate },
        ["committer"] = new { name = "CodeCoverage", email = "bot@example.invalid", date = CommitterDate },
        ["message"] = "Format the repository\n",
        ["format"] = mode,
        ["patch"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(patch)),
        ["exclude"] = exclude,
    };

    private async Task<DependencyJobResponse> PostAsync(Dictionary<string, object?> job)
    {
        var response = await Writer.Client.PostAsJsonAsync("/jobs/onboarding", job, GitWriterHost.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DependencyJobResponse>(GitWriterHost.Json))!;
    }

    [Theory]
    [InlineData("format")]
    [InlineData("lint")]
    public async Task M5_the_patch_is_one_commit_on_the_mode_s_own_branch(string mode)
    {
        var (origin, baseSha) = Seeded();

        var answer = await PostAsync(Job(origin, baseSha, mode, Edit("src/A.cs", Unformatted, Formatted)));

        answer.Outcome.Should().Be("pushed", answer.Detail);
        answer.Paths.Should().Equal("src/A.cs");
        origin.Ref($"refs/heads/{DependencyBranches.FormatOf(mode)}").Should().Be(answer.Sha);
        origin.InBare("diff-tree", "-r", "--name-status", "--no-commit-id", baseSha, answer.Sha!).ReplaceLineEndings("\n").Trim().Should().Be("M\tsrc/A.cs");
        origin.InBare("show", $"{answer.Sha}:src/A.cs").Should().Be(Formatted);
    }

    [Fact]
    public async Task D7_the_on_demand_patch_may_format_a_workflow()
    {
        var (origin, baseSha) = Seeded();

        var answer = await PostAsync(Job(origin, baseSha, "format", Edit(".github/workflows/ci.yml", "on:  push", "on: push")));

        answer.Outcome.Should().Be("pushed", answer.Detail);
        answer.Paths.Should().Equal(".github/workflows/ci.yml");
    }

    [Fact]
    public async Task M5_a_rerun_with_the_same_patch_keeps_the_branch_s_commit()
    {
        var (origin, baseSha) = Seeded();
        var first = await PostAsync(Job(origin, baseSha, "format", Edit("src/A.cs", Unformatted, Formatted)));

        var second = await PostAsync(Job(origin, baseSha, "format", Edit("src/A.cs", Unformatted, Formatted)));

        second.Outcome.Should().Be("pushed", second.Detail);
        second.Sha.Should().Be(first.Sha);
    }

    [Theory]
    [InlineData("*.g.cs")]            // no '/': any depth, as the action reads it
    [InlineData("src/")]              // a directory
    [InlineData("./src/**")]
    public async Task D11_an_excluded_file_is_refused(string exclude)
    {
        var (origin, baseSha) = Seeded();

        var answer = await PostAsync(Job(origin, baseSha, "format", Edit("src/A.g.cs", Unformatted, Formatted), exclude));

        answer.Outcome.Should().Be(JobOutcomes.PatchRejected);
        answer.Reason.Should().Be(PatchReasons.OutsideRegeneratedPaths);
        answer.Paths.Should().Equal("src/A.g.cs");
        origin.Ref($"refs/heads/{DependencyBranches.FormatOf("format")}").Should().BeNull();
    }

    [Fact]
    public async Task D15_a_new_file_is_refused()
    {
        var (origin, baseSha) = Seeded();
        const string patch = """
            diff --git a/src/B.cs b/src/B.cs
            new file mode 100644
            index 0000000..2222222
            --- /dev/null
            +++ b/src/B.cs
            @@ -0,0 +1 @@
            +class B { }

            """;

        var answer = await PostAsync(Job(origin, baseSha, "format", patch.ReplaceLineEndings("\n")));

        answer.Outcome.Should().Be(JobOutcomes.PatchRejected);
        answer.Reason.Should().Be(FormatPatchValidator.NotAnEdit);
    }

    [Fact]
    public async Task D15_a_patch_that_does_not_apply_is_refused()
    {
        var (origin, baseSha) = Seeded();

        var answer = await PostAsync(Job(origin, baseSha, "format", Edit("src/A.cs", "class Other{}", Formatted)));

        answer.Outcome.Should().Be(JobOutcomes.PatchRejected);
        answer.Reason.Should().Be(PatchReasons.ApplyFailed);
    }

    [Theory]
    [InlineData("pipeline", false)]   // the merge queue's mode never comes here
    [InlineData("format", true)]      // a format commit carries no files
    public async Task M5_only_format_or_lint_without_files_is_accepted(string mode, bool withFiles)
    {
        var (origin, baseSha) = Seeded();
        var job = Job(origin, baseSha, mode, Edit("src/A.cs", Unformatted, Formatted));
        if (withFiles)
            job["files"] = new[] { new { path = "coverage.yml", content = "x: 1\n" } };

        var response = await Writer.Client.PostAsJsonAsync("/jobs/onboarding", job, GitWriterHost.Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("*.g.cs", "src/deep/A.g.cs", true)]
    [InlineData("*.g.cs", "A.g.cs", true)]
    [InlineData("src/*.cs", "src/deep/A.cs", false)]
    [InlineData("src/", "src/deep/A.cs", true)]
    [InlineData("./vendor/**", "vendor/x/y.js", true)]
    [InlineData("**/*.Designer.cs", "Form.Designer.cs", true)]
    public void D11_exclude_globs_read_as_the_action_reads_them(string glob, string path, bool matches)
        => FormatPatchValidator.Glob(glob)!.Matches(path).Should().Be(matches);

    [Fact]
    public void D11_a_blank_exclude_entry_is_ignored() => FormatPatchValidator.Glob("  ").Should().BeNull();
}
