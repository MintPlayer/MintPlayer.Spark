using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Xunit;

namespace CodeCoverage.Tests.PrSize;

/// <summary>
/// The GitHub adapter's reading of the label and file shapes (docs/coverage_pr_size_labels_PRD.md S1, S2): an archived
/// label is seen as archived, and the files come back page by page with nothing but path and counts asked for.
/// </summary>
public class GitHubForgeLabelsTests
{
    private static readonly Repository Repo = new() { OwnerLogin = "acme", Name = "web", FullName = "acme/web" };

    [Fact]
    public void An_archived_label_reads_as_archived()
    {
        using var json = JsonDocument.Parse("""
            [{"id":1,"name":"size/M","color":"EEBB00","description":"Denotes a PR","archived_at":"2026-10-23T10:00:00Z"},
             {"id":2,"name":"bug","color":"d73a4a","description":null,"archived_at":null}]
            """);
        var labels = json.RootElement.EnumerateArray().Select(GitHubForgeLabels.ReadLabel).ToList();

        labels[0].Should().Be(new ForgeLabel("size/M", "eebb00", "Denotes a PR", true));
        labels[1].Should().Be(new ForgeLabel("bug", "d73a4a", null, false));
    }

    [Fact]
    public void The_files_request_asks_for_path_and_counts_with_every_value_a_variable()
    {
        using var body = JsonDocument.Parse(GitHubForgeLabels.FilesRequest(Repo, 7, "Y3Vyc29y"));
        var query = body.RootElement.GetProperty("query").GetString()!;
        var variables = body.RootElement.GetProperty("variables");

        query.Should().Contain("nodes { path additions deletions }");
        query.Should().NotContain("acme");
        variables.GetProperty("owner").GetString().Should().Be("acme");
        variables.GetProperty("name").GetString().Should().Be("web");
        variables.GetProperty("number").GetInt32().Should().Be(7);
        variables.GetProperty("after").GetString().Should().Be("Y3Vyc29y");
    }

    [Fact]
    public void A_page_with_more_gives_its_cursor_and_the_last_page_none()
    {
        var (files, next) = GitHubForgeLabels.ParseFiles("""
            {"data":{"repository":{"pullRequest":{"files":{"nodes":[{"path":"a.cs","additions":3,"deletions":1}],
             "pageInfo":{"hasNextPage":true,"endCursor":"MQ"}}}}}}
            """, Repo, 7);
        files.Should().ContainSingle().Which.Should().Be(new ForgePullRequestFile("a.cs", 3, 1));
        next.Should().Be("MQ");

        var (_, last) = GitHubForgeLabels.ParseFiles("""
            {"data":{"repository":{"pullRequest":{"files":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}
            """, Repo, 7);
        last.Should().BeNull();
    }

    [Fact]
    public void A_forbidden_answer_names_the_permission()
    {
        var error = Record.Exception(() => GitHubForgeLabels.ParseFiles("""
            {"data":null,"errors":[{"type":"FORBIDDEN","message":"Resource not accessible by integration"}]}
            """, Repo, 7));

        error.Should().BeOfType<ForgeAccessDeniedException>().Which.RequiredPermission.Should().Be("pull_requests=read");
    }
}
