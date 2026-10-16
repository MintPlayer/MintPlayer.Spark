using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// <see cref="GitHubForgeDependencyUpdates"/> over <see cref="StubGitHub"/>.
/// </summary>
/// <remarks>
/// <b>The READ bodies are live captures</b> (2026-10-16, after S18; <c>Fixtures/GitHub/DependencyUpdates/SOURCE.md</c>):
/// git refs and tags, contents, pulls, the artifact listing. <b>The WRITE bodies are still docs-derived</b> (marked
/// "docs:"): creating a pull request, editing it, a reaction, the check run, the artifact zip. Re-capturing them would
/// write to a repository. Owner, repository and logins are the placeholders <c>acme</c> / <c>widget</c>.
/// </remarks>
public class GitHubForgeDependencyUpdatesTests
{
    private const long InstallationId = 4242;
    private const string Repo = "/repos/acme/widget";
    private const string Commit = "6f9b0a0f8d7e5e3c2b1a09f8e7d6c5b4a3928170";

    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 77);

    private static Repository Target() => new()
    {
        Id = Entities.Repository.DocumentId(EForgeProvider.GitHub, 910040),
        GitHubId = 910040, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", Account = AccountId,
    };

    private static GitHubForgeDependencyUpdates Updates(StubGitHub github)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Account>(AccountId, Arg.Any<CancellationToken>()).Returns(new Account { GitHubId = 77, Login = "acme", InstallationId = InstallationId });
        return new GitHubForgeDependencyUpdates(github, new SingleClientHttpFactory(github.Rest), session);
    }


    private static string Captured(string name) => Fixture.Read($"GitHub/DependencyUpdates/{name}");

    // ── Gap C: tag → commit ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_lightweight_tag_resolves_to_its_commit()
    {
        // live: GET /repos/MintPlayer/MintPlayer.Spark/git/ref/tags/dependency-update-v1, the onboarding templates' pin
        var github = new StubGitHub().On(HttpMethod.Get, "/repos/MintPlayer/MintPlayer.Spark/git/ref/tags/dependency-update-v1", HttpStatusCode.OK,
            Captured("ref-tag-dependency-update-v1.json"));

        (await Updates(github).ResolveTagCommitAsync(Target(), "MintPlayer/MintPlayer.Spark", "dependency-update-v1"))
            .Should().Be("bfd709a9bf088fc45190e5f49da779129bb9ff45");
    }

    [Fact]
    public async Task A_lightweight_tag_of_an_action_resolves_to_its_commit()
    {
        // live: GET /repos/actions/checkout/git/ref/tags/v4.2.0
        var github = new StubGitHub().On(HttpMethod.Get, "/repos/actions/checkout/git/ref/tags/v4.2.0", HttpStatusCode.OK, Captured("ref-tag-lightweight.json"));

        (await Updates(github).ResolveTagCommitAsync(Target(), "actions/checkout", "v4.2.0")).Should().Be("d632683dd7b4114ad314bca15554477dd762a938");
    }

    [Fact]
    public async Task An_annotated_tag_is_dereferenced_to_its_commit()
    {
        // live: GET /repos/nodejs/node/git/ref/tags/v20.0.0 (type: tag), then GET /repos/nodejs/node/git/tags/{sha}
        var github = new StubGitHub()
            .On(HttpMethod.Get, "/repos/nodejs/node/git/ref/tags/v20.0.0", HttpStatusCode.OK, Captured("ref-tag-annotated-node.json"))
            .On(HttpMethod.Get, "/repos/nodejs/node/git/tags/ffca5a7a113131b1a252fd95b53161b5182e66be", HttpStatusCode.OK, Captured("tag-object-node.json"));

        (await Updates(github).ResolveTagCommitAsync(Target(), "nodejs/node", "v20.0.0")).Should().Be("e7618fb5a5fc25d76b6474e2a6607f04fd6f10e0");
    }

    [Theory]
    [InlineData("actions/checkout", "v9.9.9")]
    [InlineData("../etc", "v1")]
    [InlineData("actions/checkout", "v1..v2")]
    public async Task A_missing_or_malformed_tag_resolves_to_nothing(string ownerRepo, string tag)
        => (await Updates(new StubGitHub()).ResolveTagCommitAsync(Target(), ownerRepo, tag)).Should().BeNull();

    // ── R15: another repository's file, read statically ──────────────────────────────────────────

    [Fact]
    public async Task A_file_is_read_as_text()
    {
        // live: GET /repos/{o}/{r}/contents/coverage.yml?ref=master, the file onboarding PR #1 wrote
        var github = new StubGitHub().On(HttpMethod.Get, "/repos/acme/widget/contents/coverage.yml?ref=master", HttpStatusCode.OK, Captured("contents-file.json"));

        var text = await Updates(github).GetRepositoryFileAsync(Target(), "acme/widget", "master", "coverage.yml");

        text.Should().StartWith("mergeQueue:\n  enabled: true\n");
        text.Should().Contain("# dependencies:");
    }

    [Fact]
    public async Task A_directory_or_an_absent_file_reads_as_nothing()
    {
        // live: a directory answers an array; a missing file GitHub's 404 body
        var github = new StubGitHub()
            .On(HttpMethod.Get, "/repos/acme/widget/contents/.github?ref=master", HttpStatusCode.OK, Captured("contents-dir.json"))
            .On(HttpMethod.Get, "/repos/acme/widget/contents/nope.yml?ref=master", HttpStatusCode.NotFound, Captured("contents-missing.json"));
        var updates = Updates(github);
        (await updates.GetRepositoryFileAsync(Target(), "acme/widget", "master", ".github")).Should().BeNull();
        (await updates.GetRepositoryFileAsync(Target(), "acme/widget", "master", "nope.yml")).Should().BeNull();
        (await updates.GetRepositoryFileAsync(Target(), "acme/widget", "master", "../secrets")).Should().BeNull();
    }

    // ── The pull request ─────────────────────────────────────────────────────────────────────────

    private static string Pull(int number, bool draft, string state = "open") => $$$"""
        {"number":{{{number}}},"state":"{{{state}}}","draft":{{{(draft ? "true" : "false")}}},"merged":false,"merged_at":null,
         "html_url":"https://github.com/acme/widget/pull/{{{number}}}",
         "head":{"ref":"mintplayer/dependencies","sha":"{{{Commit}}}","repo":{"id":910040,"full_name":"acme/widget"}},
         "base":{"ref":"master","sha":"1111111111111111111111111111111111111111","repo":{"id":910040,"full_name":"acme/widget"} } }
        """;

    [Fact]
    public async Task The_first_pull_request_is_created_as_a_draft()
    {
        string? posted = null;
        // docs: POST /repos/{owner}/{repo}/pulls answers 201 with the pull request
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/pulls", (_, body) =>
        {
            posted = body;
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, Pull(41, draft: true));
        });

        var pull = await Updates(github).CreatePullRequestAsync(Target(), "mintplayer/dependencies", "master", "Update chalk", "body", draft: true);

        pull.Number.Should().Be(41);
        pull.IsDraft.Should().BeTrue();
        using var request = JsonDocument.Parse(posted!);
        request.RootElement.GetProperty("draft").GetBoolean().Should().BeTrue();
        request.RootElement.GetProperty("head").GetString().Should().Be("mintplayer/dependencies");
        request.RootElement.GetProperty("base").GetString().Should().Be("master");
    }

    [Fact]
    public async Task The_open_pull_request_is_found_by_its_head_branch()
    {
        // live: GET /repos/{o}/{r}/pulls?head={o}:mintplayer/dependencies — the S18 rolling PRs #3 and #2, newest first
        // (captured with state=all, since both are merged now; the adapter asks for state=open and takes the first).
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/pulls", HttpStatusCode.OK, Captured("pulls-open-by-head.json"));

        var pull = await Updates(github).FindPullRequestAsync(Target(), "mintplayer/dependencies", "master");

        github.Calls.Should().ContainSingle().Which.Should().Contain("state=open").And.Contain("head=acme").And.Contain("base=master");
        pull!.Number.Should().Be(3);
        pull.HeadRef.Should().Be("mintplayer/dependencies");
        pull.HeadSha.Should().Be("a69d651483c76bd32d85f6515b66816d34218fcd");
        pull.BaseRef.Should().Be("master");
    }

    [Fact]
    public async Task A_merged_pull_request_reads_as_merged()
    {
        // live: GET /repos/{o}/{r}/pulls/3, merged by the merge queue at 17:37:55
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/pulls/3", HttpStatusCode.OK, Captured("pull-3.json"));

        var pull = await Updates(github).GetPullRequestAsync(Target(), 3);

        pull!.IsOpen.Should().BeFalse();
        pull.IsMerged.Should().BeTrue();
        pull.IsDraft.Should().BeFalse();
        pull.HtmlUrl.Should().Be("https://github.com/acme/widget/pull/3");
    }

    [Fact]
    public async Task The_artifact_listing_is_read_as_github_sends_it()
    {
        // live: GET /repos/{o}/{r}/actions/runs/{id}/artifacts of a dependency workflow run; the zip itself stays docs-derived
        var zip = Zip(("result.json", "{}"));
        var github = new StubGitHub()
            .On(HttpMethod.Get, $"{Repo}/actions/runs/7/artifacts", HttpStatusCode.OK, Captured("run-artifacts.json"))
            .On(HttpMethod.Get, $"{Repo}/actions/artifacts/12062163661/zip", (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });

        var files = await Updates(github).DownloadArtifactFilesAsync(Target(), 7, "dependency-apply-1422440175-4-2", ["result.json"], 4096);

        files.Outcome.Should().Be(EForgeArtifactOutcome.Ok);
        files.Files.Keys.Should().Equal(["result.json"]);
        (await Updates(github).DownloadArtifactFilesAsync(Target(), 7, "dependency-apply-1422440175-4-2", ["result.json"], 1000)).Outcome
            .Should().Be(EForgeArtifactOutcome.TooLarge, "the listing declares 1482 bytes");
    }

    [Fact]
    public async Task A_missing_pull_request_is_null()
        => (await Updates(new StubGitHub()).GetPullRequestAsync(Target(), 99)).Should().BeNull();

    [Fact]
    public async Task A_refused_create_names_the_permission()
    {
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/pulls", HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration","status":"403"}""");

        var act = () => Updates(github).CreatePullRequestAsync(Target(), "mintplayer/dependencies", "master", "t", "b", false);

        (await act.Should().ThrowAsync<ForgeAccessDeniedException>()).Which.RequiredPermission.Should().Be("pull_requests=write");
    }

    [Theory]
    [InlineData(EForgeReaction.ThumbsUp, "+1")]
    [InlineData(EForgeReaction.ThumbsDown, "-1")]
    public async Task A_command_gets_its_reaction(EForgeReaction reaction, string content)
    {
        string? posted = null;
        // docs: POST /repos/{owner}/{repo}/issues/comments/{comment_id}/reactions
        var github = new StubGitHub().On(HttpMethod.Post, $"{Repo}/issues/comments/900/reactions", (_, body) =>
        {
            posted = body;
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, $$$"""{"id":1,"content":"{{{content}}}","user":{"login":"x","id":1}}""");
        });

        await Updates(github).AddCommentReactionAsync(Target(), 900, reaction);

        using var request = JsonDocument.Parse(posted!);
        request.RootElement.GetProperty("content").GetString().Should().Be(content);
    }

    // ── The check run ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_check_run_stays_in_progress_while_bisecting_and_completes_otherwise()
    {
        var bodies = new List<string>();
        var github = new StubGitHub()
            .On(HttpMethod.Post, $"{Repo}/check-runs", (_, body) => { bodies.Add(body!); return StubHttpMessageHandler.Json(HttpStatusCode.Created, """{"id":700,"name":"Dependency updates"}"""); })
            .On(HttpMethod.Patch, $"{Repo}/check-runs/700", (_, body) => { bodies.Add(body!); return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":700,"name":"Dependency updates"}"""); });
        var updates = Updates(github);

        var id = await updates.PublishCheckAsync(Target(), Commit, new ForgeQueueCheck(EForgeQueueCheckState.InProgress, "Bisecting: don't merge", "s", null), null);
        await updates.PublishCheckAsync(Target(), Commit, new ForgeQueueCheck(EForgeQueueCheckState.Success, "CI is green", "s", null) { PullRequestNumber = 41 }, id);

        id.Should().Be(700);
        using var created = JsonDocument.Parse(bodies[0]);
        created.RootElement.GetProperty("status").GetString().Should().Be("in_progress");
        created.RootElement.GetProperty("head_sha").GetString().Should().Be(Commit);
        created.RootElement.TryGetProperty("conclusion", out _).Should().BeFalse();
        using var updated = JsonDocument.Parse(bodies[1]);
        updated.RootElement.GetProperty("status").GetString().Should().Be("completed");
        updated.RootElement.GetProperty("conclusion").GetString().Should().Be("success");
        updated.RootElement.GetProperty("external_id").GetString().Should().Be("41");
    }

    // ── Artifacts with several files ─────────────────────────────────────────────────────────────

    private static byte[] Zip(params (string Name, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return buffer.ToArray();
    }

    private static StubGitHub WithArtifact(byte[] zip) => new StubGitHub()
        .On(HttpMethod.Get, $"{Repo}/actions/runs/7/artifacts?name=dependency-apply-1&per_page=100", HttpStatusCode.OK, $$$"""
            {"total_count":1,"artifacts":[{"id":5,"node_id":"MDg6QXJ0aWZhY3Q1","name":"dependency-apply-1","size_in_bytes":{{{zip.Length}}},
             "url":"https://api.github.com/repos/acme/widget/actions/artifacts/5","archive_download_url":"https://api.github.com/repos/acme/widget/actions/artifacts/5/zip",
             "expired":false,"created_at":"2026-10-16T08:00:00Z","expires_at":"2026-10-19T08:00:00Z","updated_at":"2026-10-16T08:00:00Z"}]}
            """)
        .On(HttpMethod.Get, $"{Repo}/actions/artifacts/5/zip", (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });

    [Fact]
    public async Task The_named_files_of_an_artifact_are_read_and_others_ignored()
    {
        var github = WithArtifact(Zip(("result.json", "{}"), ("dependency.patch", "diff"), ("log.txt", "noise")));

        var files = await Updates(github).DownloadArtifactFilesAsync(Target(), 7, "dependency-apply-1", ["result.json", "dependency.patch"], 1024);

        files.Outcome.Should().Be(EForgeArtifactOutcome.Ok);
        files.Files.Keys.Should().BeEquivalentTo(["result.json", "dependency.patch"]);
        Encoding.UTF8.GetString(files.Files["dependency.patch"]).Should().Be("diff");
    }

    [Fact]
    public async Task Files_over_the_cap_are_refused()
    {
        var github = WithArtifact(Zip(("result.json", new string('x', 600)), ("dependency.patch", new string('y', 600))));

        var files = await Updates(github).DownloadArtifactFilesAsync(Target(), 7, "dependency-apply-1", ["result.json", "dependency.patch"], 1000);

        files.Outcome.Should().Be(EForgeArtifactOutcome.TooLarge);
    }

    [Fact]
    public void A_name_twice_in_the_archive_is_invalid()
    {
        GitHubForgeDependencyUpdates.ReadNamedFiles(Zip(("result.json", "{}"), ("result.json", "{}")), ["result.json"], 1024).Outcome
            .Should().Be(EForgeArtifactOutcome.Invalid);
    }
}
