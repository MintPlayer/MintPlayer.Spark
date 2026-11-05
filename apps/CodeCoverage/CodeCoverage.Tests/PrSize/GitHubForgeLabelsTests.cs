using System.Net;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using CodeCoverage.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
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
    public void The_existing_size_check_is_found_by_its_id_so_a_retry_updates_it()
    {
        using var found = JsonDocument.Parse("""{"total_count":1,"check_runs":[{"id":4711,"name":"PR size","status":"in_progress"}]}""");
        using var none = JsonDocument.Parse("""{"total_count":0,"check_runs":[]}""");

        GitHubForgeLabels.ReadCheckRunId(found.RootElement).Should().Be(4711);
        GitHubForgeLabels.ReadCheckRunId(none.RootElement).HasValue.Should().BeFalse();
    }

    [Fact]
    public void A_check_without_text_leaves_the_property_out()
    {
        // GitHub answers 422 to "text": null (found in the live test on PR #534).
        using var started = JsonDocument.Parse(GitHubForgeLabels.SizeCheckBody("abc", new ForgeSizeCheck(false, "Counting", "Counting the lines", null)).ToJsonString());
        using var done = JsonDocument.Parse(GitHubForgeLabels.SizeCheckBody("abc", new ForgeSizeCheck(true, "size/M", "40 lines", "the table")).ToJsonString());

        started.RootElement.GetProperty("status").GetString().Should().Be("in_progress");
        started.RootElement.TryGetProperty("conclusion", out _).Should().BeFalse();
        started.RootElement.GetProperty("output").TryGetProperty("text", out _).Should().BeFalse();
        done.RootElement.GetProperty("conclusion").GetString().Should().Be("neutral");
        done.RootElement.GetProperty("output").GetProperty("text").GetString().Should().Be("the table");
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

    [Fact]
    public void A_rate_limited_or_other_graphql_error_is_a_request_error()
    {
        var limited = Record.Exception(() => GitHubForgeLabels.ParseFiles("""
            {"data":null,"errors":[{"type":"RATE_LIMITED","message":"API rate limit exceeded"}]}
            """, Repo, 7));
        var other = Record.Exception(() => GitHubForgeLabels.ParseFiles("""
            {"data":null,"errors":[{"type":"NOT_FOUND","message":"Could not resolve to a PullRequest with the number of 7."}]}
            """, Repo, 7));

        limited.Should().BeOfType<ForgeRequestException>().Which.StatusCode.Should().Be(429);
        other.Should().BeOfType<ForgeRequestException>().Which.Message
            .Should().Be("GitHub refused listing the files of acme/web#7: Could not resolve to a PullRequest with the number of 7.");
    }

    // ── Against a scripted GitHub ────────────────────────────────────────────────────────────────

    private const string Path = "/repos/acme/widget";
    private const string Sha = "4a5b6c7d8e9f00112233445566778899aabbccdd";
    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 77);

    private static readonly Repository Installed = new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, 910040), GitHubId = 910040, Name = "widget", FullName = "acme/widget",
        OwnerLogin = "acme", Account = AccountId,
    };

    private static readonly ForgeLabelDefinition SizeM = new("size/M", "eebb00", "30-99 changed lines");

    private static GitHubForgeLabels Forge(StubGitHub github)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Account>(AccountId, Arg.Any<CancellationToken>()).Returns(new Account { GitHubId = 77, Login = "acme", InstallationId = 4242 });
        return new GitHubForgeLabels(github, new SingleClientHttpFactory(github.Rest), session, NullLogger<GitHubForgeLabels>.Instance);
    }

    private static string Labels(int from, int count)
        => "[" + string.Join(",", Enumerable.Range(from, count).Select(i => $$"""{"name":"l{{i}}","color":"AABBCC","description":null}""")) + "]";

    private static async Task<T> Throws<T>(Func<Task> act) where T : Exception
        => (await act.Should().ThrowAsync<T>()).Which;

    [Fact]
    public async Task The_repository_labels_are_read_page_by_page_until_a_short_page()
    {
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Path}/labels?per_page=100&page=1", HttpStatusCode.OK, Labels(0, 100))
            .On(HttpMethod.Get, $"{Path}/labels?per_page=100&page=2", HttpStatusCode.OK, Labels(100, 1));

        var labels = await Forge(github).ListRepositoryLabelsAsync(Installed);

        labels.Should().HaveCount(101);
        labels[100].Should().Be(new ForgeLabel("l100", "aabbcc", null, false));
        github.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_repository_with_more_than_the_page_limit_stops_at_the_limit()
    {
        var github = new StubGitHub();
        for (var page = 1; page <= 21; page++)
            github.On(HttpMethod.Get, $"{Path}/labels?per_page=100&page={page}", HttpStatusCode.OK, Labels(page * 100, 100));

        (await Forge(github).ListRepositoryLabelsAsync(Installed)).Should().HaveCount(2_000);
        github.Calls.Should().HaveCount(20);
    }

    [Fact]
    public async Task A_label_is_created_once_and_a_second_create_is_a_conflict()
    {
        var bodies = new List<string?>();
        var created = new StubGitHub().On(HttpMethod.Post, $"{Path}/labels", (_, body) =>
        {
            bodies.Add(body);
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """{"id":1}""");
        });
        (await Forge(created).CreateLabelAsync(Installed, SizeM)).Should().Be(ForgeLabelWrite.Done);
        bodies.Should().Equal("""{"name":"size/M","color":"eebb00","description":"30-99 changed lines"}""");

        var exists = new StubGitHub().On(HttpMethod.Post, $"{Path}/labels", HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"resource":"Label","code":"already_exists","field":"name"}]}""");
        (await Forge(exists).CreateLabelAsync(Installed, SizeM)).Should().Be(ForgeLabelWrite.Conflict);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}", ForgeLabelWrite.Done)]
    [InlineData(HttpStatusCode.NotFound, StubGitHub.NotFoundBody, ForgeLabelWrite.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed","errors":[{"code":"already_exists"}]}""", ForgeLabelWrite.Conflict)]
    public async Task A_label_update_renames_by_its_escaped_current_name(HttpStatusCode status, string json, ForgeLabelWrite expected)
    {
        var bodies = new List<string?>();
        var github = new StubGitHub().On(HttpMethod.Patch, $"{Path}/labels/size%2FM", (_, body) => { bodies.Add(body); return StubHttpMessageHandler.Json(status, json); });

        (await Forge(github).UpdateLabelAsync(Installed, "size/M", SizeM with { Name = "size/medium" })).Should().Be(expected);
        bodies.Should().ContainSingle().Which.Should().Contain("\"new_name\":\"size/medium\"");
    }

    [Fact]
    public async Task A_label_update_with_another_422_is_an_error()
    {
        var github = new StubGitHub().On(HttpMethod.Patch, $"{Path}/labels/size%2FM", HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"code":"invalid","field":"color"}]}""");

        (await Throws<ForgeRequestException>(() => Forge(github).UpdateLabelAsync(Installed, "size/M", SizeM))).StatusCode.Should().Be(422);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, ForgeLabelWrite.Done)]
    [InlineData(HttpStatusCode.NotFound, ForgeLabelWrite.NotFound)]
    public async Task A_label_is_deleted_or_was_gone(HttpStatusCode status, ForgeLabelWrite expected)
    {
        var github = new StubGitHub().On(HttpMethod.Delete, $"{Path}/labels/size%2FM",
            (_, _) => status == HttpStatusCode.NotFound ? StubHttpMessageHandler.Json(status, StubGitHub.NotFoundBody) : new HttpResponseMessage(status));

        (await Forge(github).DeleteLabelAsync(Installed, "size/M")).Should().Be(expected);
    }

    [Fact]
    public async Task A_pull_requests_labels_are_listed_added_and_removed()
    {
        var added = new List<string?>();
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Path}/issues/7/labels?per_page=100", HttpStatusCode.OK, """[{"name":"size/S"},{"name":"bug"}]""")
            .On(HttpMethod.Post, $"{Path}/issues/7/labels", (_, body) => { added.Add(body); return StubHttpMessageHandler.Json(HttpStatusCode.OK, "[]"); })
            .On(HttpMethod.Delete, $"{Path}/issues/7/labels/size%2FS", HttpStatusCode.OK, "[]");
        var forge = Forge(github);

        (await forge.ListPullRequestLabelsAsync(Installed, 7)).Should().Equal("size/S", "bug");
        await forge.AddLabelAsync(Installed, 7, "size/M");
        (await forge.RemoveLabelAsync(Installed, 7, "size/S")).Should().BeTrue();
        (await forge.RemoveLabelAsync(Installed, 7, "size/XL")).Should().BeFalse("a label the pull request does not carry is already the state we wanted");

        added.Should().Equal("""{"labels":["size/M"]}""");
    }

    [Fact]
    public async Task The_files_of_a_pull_request_are_read_page_by_page_over_graphql()
    {
        var github = new StubGitHub()
            .OnGraphQL("""{"data":{"repository":{"pullRequest":{"files":{"nodes":[{"path":"a.cs","additions":3,"deletions":1}],"pageInfo":{"hasNextPage":true,"endCursor":"MQ"}}}}}}""")
            .OnGraphQL("""{"data":{"repository":{"pullRequest":{"files":{"nodes":[{"path":"b.cs","additions":0,"deletions":9}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}""");

        var files = await Forge(github).ListPullRequestFilesAsync(Installed, 7);

        files.Should().Equal(new ForgePullRequestFile("a.cs", 3, 1), new ForgePullRequestFile("b.cs", 0, 9));
        github.GraphQLBodies.Should().HaveCount(2);
        github.GraphQLBodies[1].Should().Contain("\"after\":\"MQ\"");
    }

    [Fact]
    public async Task The_size_check_is_found_and_patched_or_created()
    {
        var find = $"{Path}/commits/{Sha}/check-runs?check_name=PR%20size&filter=latest&per_page=1";
        var found = new StubGitHub()
            .On(HttpMethod.Get, find, HttpStatusCode.OK, """{"total_count":1,"check_runs":[{"id":4711}]}""")
            .On(HttpMethod.Patch, $"{Path}/check-runs/4711", HttpStatusCode.OK, """{"id":4711}""");
        var none = new StubGitHub()
            .On(HttpMethod.Get, find, HttpStatusCode.OK, """{"total_count":0,"check_runs":[]}""")
            .On(HttpMethod.Post, $"{Path}/check-runs", HttpStatusCode.Created, """{"id":4712}""");
        var check = new ForgeSizeCheck(true, "size/M", "40 lines", null);

        (await Forge(found).PublishSizeCheckAsync(Installed, Sha, check, null)).Should().Be(4711);
        (await Forge(none).PublishSizeCheckAsync(Installed, Sha, check, null)).Should().Be(4712);
    }

    [Fact]
    public async Task A_refused_check_write_names_the_checks_permission()
    {
        var github = new StubGitHub().On(HttpMethod.Patch, $"{Path}/check-runs/4711", HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");

        var thrown = await Throws<ForgeAccessDeniedException>(() => Forge(github).PublishSizeCheckAsync(Installed, Sha, new ForgeSizeCheck(false, "t", "s", null), 4711));

        thrown.RequiredPermission.Should().Be("checks=write");
    }

    [Fact]
    public async Task A_repository_without_an_installation_is_refused_before_any_call()
    {
        var github = new StubGitHub();
        var orphan = new Repository { Id = Installed.Id, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", Account = null };

        (await Throws<ForgeAccessDeniedException>(() => Forge(github).ListPullRequestFilesAsync(orphan, 7))).Message.Should().Be("No GitHub App installation for acme/widget.");
        github.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_rate_limit_is_retryable_and_a_proxy_page_is_reported_by_its_status()
    {
        var limited = new StubGitHub().On(HttpMethod.Get, $"{Path}/issues/7/labels?per_page=100", (_, _) =>
        {
            var response = StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, """{"message":"API rate limit exceeded for installation ID 4242."}""");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            return response;
        });
        var proxy = new StubGitHub().On(HttpMethod.Post, $"{Path}/issues/7/labels",
            (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502</html>") });
        var forbidden = new StubGitHub().On(HttpMethod.Delete, $"{Path}/labels/x", (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));

        (await Throws<ForgeRequestException>(() => Forge(limited).ListPullRequestLabelsAsync(Installed, 7))).StatusCode.Should().Be(429);
        (await Throws<ForgeRequestException>(() => Forge(proxy).AddLabelAsync(Installed, 7, "size/M"))).Message
            .Should().Be("POST repos/acme/widget/issues/7/labels answered 502: ");
        (await Throws<ForgeAccessDeniedException>(() => Forge(forbidden).DeleteLabelAsync(Installed, "x"))).Message
            .Should().Be("DELETE repos/acme/widget/labels/x was forbidden.");
    }
}
