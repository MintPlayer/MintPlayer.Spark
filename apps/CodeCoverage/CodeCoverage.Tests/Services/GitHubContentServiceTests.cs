using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// Content is cached only at a full SHA. A cached read by branch name served the onboarding's pre-merge coverage.yml to
/// the release run of the merge ("coverage.yml on master has no versioning section", SpaServices run 40132022816).
/// </summary>
public class GitHubContentServiceTests
{
    private const long RepositoryId = 4242;
    private const long InstallationId = 99;
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly Repository Widget = new() { GitHubId = RepositoryId, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme" };

    private static (GitHubContentService Service, StubGitHub GitHub, Action<string> SetBody) Serve(string initial)
    {
        var body = initial;
        var github = new StubGitHub().On(HttpMethod.Get, $"/repositories/{RepositoryId}/contents/coverage.yml",
            (_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, ContentJson(body)));
        var service = new GitHubContentService(github, new SingleClientHttpFactory(github.Rest), new SourceContentCache(), NullLogger<GitHubContentService>.Instance);
        return (service, github, value => body = value);
    }

    private static string ContentJson(string text) => $$"""
        {
          "type": "file",
          "encoding": "base64",
          "size": {{text.Length}},
          "name": "coverage.yml",
          "path": "coverage.yml",
          "content": "{{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text))}}",
          "sha": "3d21ec53a331a6f037a91c368710b99387d012c1",
          "url": "https://api.github.com/repos/acme/widget/contents/coverage.yml",
          "git_url": "https://api.github.com/repos/acme/widget/git/blobs/3d21ec53a331a6f037a91c368710b99387d012c1",
          "html_url": "https://github.com/acme/widget/blob/main/coverage.yml",
          "download_url": "https://raw.githubusercontent.com/acme/widget/main/coverage.yml",
          "_links": { "self": "https://api.github.com/repos/acme/widget/contents/coverage.yml", "git": "https://api.github.com/repos/acme/widget/git/blobs/3d21ec53a331a6f037a91c368710b99387d012c1", "html": "https://github.com/acme/widget/blob/main/coverage.yml" }
        }
        """;

    [Fact]
    public async Task A_read_by_branch_name_sees_the_branch_move()
    {
        var (service, github, setBody) = Serve("gate: {}\n");

        (await service.GetFileContentAsync(Widget, InstallationId, "master", "coverage.yml")).Should().Be("gate: {}\n");
        setBody("gate: {}\nversioning:\n  mode: server\n");

        (await service.GetFileContentAsync(Widget, InstallationId, "master", "coverage.yml")).Should().Be("gate: {}\nversioning:\n  mode: server\n");
        github.Calls.Count(c => c.Contains("/contents/")).Should().Be(2);
    }

    [Fact]
    public async Task A_read_at_a_full_sha_is_cached()
    {
        var (service, github, setBody) = Serve("gate: {}\n");

        (await service.GetFileContentAsync(Widget, InstallationId, Sha, "coverage.yml")).Should().Be("gate: {}\n");
        setBody("changed\n");

        (await service.GetFileContentAsync(Widget, InstallationId, Sha, "coverage.yml")).Should().Be("gate: {}\n");
        github.Calls.Count(c => c.Contains("/contents/")).Should().Be(1);
    }
}
