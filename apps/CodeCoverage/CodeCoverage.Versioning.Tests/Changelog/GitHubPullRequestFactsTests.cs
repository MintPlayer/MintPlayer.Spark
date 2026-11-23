using System.Net;
using System.Text;
using System.Text.Json;
using CodeCoverage.Versioning.Changelog;

namespace CodeCoverage.Versioning.Tests.Changelog;

/// <summary>
/// #693 D5, D9: what GitHub says about the pull requests of a release. A stub handler answers the GraphQL queries, so the
/// batching, the request and the parsing are checked without a network.
/// </summary>
public class GitHubPullRequestFactsTests
{
    private sealed class Stub(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Asked.Add((request, body));
            return answer(request, body);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>A GraphQL answer with one pull request per number in the query, labelled "enhancement".</summary>
    private static HttpResponseMessage EchoPulls(string body)
    {
        var query = JsonDocument.Parse(body).RootElement.GetProperty("query").GetString()!;
        var numbers = System.Text.RegularExpressions.Regex.Matches(query, @"pullRequest\(number: (\d+)\)").Select(m => int.Parse(m.Groups[1].Value));
        var pulls = string.Join(',', numbers.Select(n => $$"""
            "pr{{n}}": { "number": {{n}}, "title": "t{{n}}", "body": null, "labels": { "nodes": [{ "name": "enhancement" }] }, "closingIssuesReferences": { "nodes": [] } }
            """));
        return Json($$"""{ "data": { "repository": { {{pulls}} } } }""");
    }

    [Theory]
    [InlineData("https://api.github.com", "https://api.github.com/graphql")]
    [InlineData("https://api.github.com/", "https://api.github.com/graphql")]
    [InlineData("https://ghe.example.test/api/v3", "https://ghe.example.test/api/graphql")]
    [InlineData("https://ghe.example.test/api/v3/", "https://ghe.example.test/api/graphql")]
    public void The_graphql_url_follows_the_rest_root(string api, string expected)
        => GitHubPullRequestFacts.GraphQlUrl(api).Should().Be(expected);

    [Fact]
    public async Task Queries_in_batches_with_the_token_and_the_repository_as_variables()
    {
        var stub = new Stub((_, body) => EchoPulls(body));
        var facts = new GitHubPullRequestFacts(new HttpClient(stub), "https://api.github.test/graphql", "octo-org/octo-repo", "tok", _ => { });

        var numbers = Enumerable.Range(1, GitHubPullRequestFacts.BatchSize + 5).Concat([0, -3, 7]).ToArray();
        var answer = await facts.GetAsync(numbers);

        answer.Keys.Should().BeEquivalentTo(Enumerable.Range(1, GitHubPullRequestFacts.BatchSize + 5));
        answer[7].Should().BeEquivalentTo(new PullRequestFacts(7, "t7", null, ["enhancement"]));
        stub.Asked.Should().HaveCount(2);
        var (request, body) = stub.Asked[0];
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsoluteUri.Should().Be("https://api.github.test/graphql");
        request.Headers.Authorization!.ToString().Should().Be("Bearer tok");
        request.Headers.UserAgent.ToString().Should().Contain("codecoverage-versioning");
        var variables = JsonDocument.Parse(body).RootElement.GetProperty("variables");
        (variables.GetProperty("owner").GetString(), variables.GetProperty("name").GetString()).Should().Be(("octo-org", "octo-repo"));
        JsonDocument.Parse(body).RootElement.GetProperty("query").GetString().Should().NotContain("octo-org");
    }

    [Theory]
    [InlineData("octo-repo")]
    [InlineData("/octo-repo")]
    [InlineData("octo-org/")]
    [InlineData("a/b/c")]
    public async Task A_repository_that_is_not_owner_slash_name_asks_nothing(string repository)
    {
        var stub = new Stub((_, body) => EchoPulls(body));
        var facts = new GitHubPullRequestFacts(new HttpClient(stub), "https://api.github.test/graphql", repository, "tok", _ => { });

        (await facts.GetAsync([1, 2])).Should().BeEmpty();
        stub.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_batch_warns_and_leaves_only_its_pull_requests_out()
    {
        var calls = 0;
        var stub = new Stub((_, body) => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.BadGateway) : EchoPulls(body));
        var warnings = new List<string>();
        var facts = new GitHubPullRequestFacts(new HttpClient(stub), "https://api.github.test/graphql", "octo-org/octo-repo", "tok", warnings.Add);

        var answer = await facts.GetAsync(Enumerable.Range(1, GitHubPullRequestFacts.BatchSize + 1).ToArray());

        answer.Keys.Should().Equal(GitHubPullRequestFacts.BatchSize + 1);
        warnings.Should().ContainSingle().Which.Should().StartWith("Reading pull requests #1, #2,").And.Contain("commit subjects");
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_a_warning_too()
    {
        var stub = new Stub((_, _) => Json("<html>rate limited</html>"));
        var warnings = new List<string>();
        var facts = new GitHubPullRequestFacts(new HttpClient(stub), "https://api.github.test/graphql", "octo-org/octo-repo", "tok", warnings.Add);

        (await facts.GetAsync([4])).Should().BeEmpty();
        warnings.Should().ContainSingle().Which.Should().StartWith("Reading pull requests #4 failed");
    }

    [Fact]
    public void Reads_labels_a_linked_bug_and_the_backport_marker_and_skips_issues_and_odd_shapes()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": { "repository": {
                "pr10": { "number": 10, "title": "Fix it", "body": "x\n<!-- backport-of: Octo-Org/octo-repo#9 -->",
                          "labels": { "nodes": [{ "name": "backport" }, { "nope": 1 }, 5] },
                          "closingIssuesReferences": { "nodes": [{ "labels": { "nodes": [{ "name": "question" }] } }, { "labels": { "nodes": [{ "name": "BUG" }] } }] } },
                "pr11": { "number": 11, "title": null, "body": "<!-- backport-of: other/repo#3 -->" },
                "pr12": null,
                "pr13": { "number": "13" },
                "pr14": "text",
                "pr15": { "number": 15, "title": "t", "labels": { "nodes": "none" }, "closingIssuesReferences": [] }
              } },
              "errors": [{ "message": "Could not resolve to a PullRequest with the number of 12." }]
            }
            """);

        var pulls = GitHubPullRequestFacts.Read(document.RootElement, "octo-org/octo-repo");

        pulls.Select(p => p.Number).Should().Equal(10, 11, 15);
        pulls[0].Should().BeEquivalentTo(new PullRequestFacts(10, "Fix it", "x\n<!-- backport-of: Octo-Org/octo-repo#9 -->", ["backport"], LinkedIssueIsBug: true, BackportOf: 9));
        (pulls[1].Title, pulls[1].Labels.Count, pulls[1].LinkedIssueIsBug, pulls[1].BackportOf).Should().Be(("", 0, false, (int?)null));
        (pulls[2].Labels.Count, pulls[2].LinkedIssueIsBug).Should().Be((0, false));
    }

    [Theory]
    [InlineData("""{ "errors": [] }""")]
    [InlineData("""{ "data": null }""")]
    [InlineData("""{ "data": { "repository": null } }""")]
    public void An_answer_without_a_repository_has_no_pull_requests(string json)
    {
        using var document = JsonDocument.Parse(json);
        GitHubPullRequestFacts.Read(document.RootElement, "octo-org/octo-repo").Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("no marker", null)]
    [InlineData("<!-- backport-of: #609 -->", 609)]
    [InlineData("<!--backport-of:octo-org/octo-repo#42-->", 42)]
    [InlineData("<!-- backport-of: octo-org/other#42 -->", null)]
    [InlineData("<!-- backport-of: #0 -->", null)]
    public void The_backport_marker_names_an_original_of_this_repository_only(string? body, int? expected)
        => GitHubPullRequestFacts.BackportOf(body, "octo-org/octo-repo").Should().Be(expected);
}
