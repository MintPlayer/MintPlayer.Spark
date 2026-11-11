using System.Net;
using CodeCoverage.GitWriter.Jobs;
using CodeCoverage.GitWriter.Tests._Infrastructure;

namespace CodeCoverage.GitWriter.Tests;

/// <summary>
/// Segment G: the backport a merge would start, tried before the merge. The queued commit is not on the default branch
/// yet, so it comes through <c>refs/pull/N/head</c>; each line is merged as the backport would merge it, and nothing is
/// pushed anywhere.
/// </summary>
public class BackportTrialTests : WriterTestBase
{
    private static Dictionary<string, object?> Request(Origin origin, string @base, string commit, params string[] lines) => new()
    {
        ["provider"] = "github",
        ["repoId"] = "1001",
        ["cloneUrl"] = origin.Url.AbsoluteUri,
        ["credential"] = Credential(Token),
        ["headRef"] = "refs/pull/7/head",
        ["base"] = @base,
        ["commit"] = commit,
        ["targets"] = lines.Select(l => new { branch = $"release/{l}" }).ToArray(),
    };

    /// <summary>main at the seed with <c>release/1.x</c> and <c>release/2.x</c> cut from it; 2.x then edits the README.</summary>
    private (Origin Origin, string Base) Lines()
    {
        var origin = Seed();
        origin.Checkout("main");
        origin.Push("main");
        origin.Checkout("release/1.x", "main").Push("release/1.x");
        origin.Checkout("release/2.x", "main").Write("README.md", "two\n");
        origin.Commit("2.x: readme");
        origin.Push("release/2.x");
        origin.Checkout("main");
        return (origin, origin.Ref("refs/heads/main")!);
    }

    /// <summary>The pull request's commit on <c>trial-feature</c>, served as <c>refs/pull/7/head</c> and not on main.</summary>
    private static string Feature(Origin origin, string path, string content)
    {
        origin.Checkout("trial-feature", "main").Write(path, content);
        var commit = origin.Commit("feat: change (#7)");
        origin.PullRequest(7, "trial-feature");
        origin.Checkout("main");
        return commit;
    }

    [Fact]
    public async Task Each_line_gets_its_own_outcome_and_nothing_is_pushed()
    {
        var (origin, @base) = Lines();
        var commit = Feature(origin, "README.md", "fixed\n");
        var before = (origin.Ref("refs/heads/release/1.x"), origin.Ref("refs/heads/release/2.x"));

        var response = await Writer.BackportTrialAsync(Request(origin, @base, commit, "1.x", "2.x"));

        response.Outcome.Should().Be(JobOutcomes.Tried, response.Detail);
        response.Targets.Single(t => t.Branch == "release/1.x").Outcome.Should().Be(BackportService.Clean);
        var conflicted = response.Targets.Single(t => t.Branch == "release/2.x");
        conflicted.Outcome.Should().Be(BackportService.Conflicted);
        conflicted.Conflicts.Should().Equal("README.md");
        (origin.Ref("refs/heads/release/1.x"), origin.Ref("refs/heads/release/2.x")).Should().Be(before);
        origin.Ref("refs/heads/backport/7-1.x").Should().BeNull("a trial pushes no backport branch");
        origin.Ref("refs/heads/main").Should().Be(@base);
    }

    [Fact]
    public async Task A_line_that_already_holds_the_change_is_empty()
    {
        var (origin, @base) = Lines();
        var commit = Feature(origin, "README.md", "two\n");

        var response = await Writer.BackportTrialAsync(Request(origin, @base, commit, "2.x"));

        response.Targets.Single().Outcome.Should().Be(BackportService.Empty);
    }

    [Fact]
    public async Task A_head_that_moved_is_a_mismatch()
    {
        var (origin, @base) = Lines();
        Feature(origin, "fix.txt", "fixed\n");

        var response = await Writer.BackportTrialAsync(Request(origin, @base, new string('a', 40), "1.x"));

        response.Outcome.Should().Be(JobOutcomes.HeadMismatch);
        response.Targets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_base_the_commit_does_not_contain_is_unplannable()
    {
        var (origin, _) = Lines();
        var commit = Feature(origin, "fix.txt", "fixed\n");

        var response = await Writer.BackportTrialAsync(Request(origin, origin.Ref("refs/heads/release/2.x")!, commit, "1.x"));

        response.Outcome.Should().Be(JobOutcomes.Failed);
        response.Reason.Should().Be(JobReasons.Unplannable);
    }

    [Theory]
    [InlineData("targets")]
    [InlineData("base")]
    [InlineData("headRef")]
    public async Task A_malformed_request_is_refused(string field)
    {
        var (origin, @base) = Lines();
        var request = Request(origin, @base, new string('c', 40), "1.x");
        request[field] = field == "targets" ? new[] { new { branch = "release/1.x" }, new { branch = "release/1.x" } } : "refs/heads/../x";

        var response = await Writer.PostBackportTrialAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(field);
    }
}
