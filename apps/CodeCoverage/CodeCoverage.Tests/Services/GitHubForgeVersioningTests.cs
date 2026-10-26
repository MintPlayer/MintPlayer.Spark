using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>The GitHub half of <see cref="IForgeVersioning"/>, against a scripted REST API.</summary>
public class GitHubForgeVersioningTests
{
    private const string Sha = "4a5b6c7d8e9f00112233445566778899aabbccdd";
    private const string Repo = "/repos/acme/widget";
    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 77);

    private static readonly Repository Target = new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, 910040), GitHubId = 910040, Name = "widget", FullName = "acme/widget",
        OwnerLogin = "acme", Account = AccountId,
    };

    private static GitHubForgeVersioning Forge(StubGitHub github)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Account>(AccountId, Arg.Any<CancellationToken>()).Returns(new Account { GitHubId = 77, Login = "acme", InstallationId = 4242 });
        return new GitHubForgeVersioning(github, new SingleClientHttpFactory(github.Rest), session);
    }

    [Fact]
    public async Task A_commit_names_only_its_pull_requests_merged_into_the_branch()
    {
        // The commit's pull requests include an open one that contains it and one merged into a release branch.
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/commits/{Sha}/pulls?per_page=10", HttpStatusCode.OK, """
            [{"number":203,"merged_at":"2026-10-26T09:30:00Z","base":{"ref":"master"}},
             {"number":204,"merged_at":null,"base":{"ref":"master"}},
             {"number":205,"merged_at":"2026-10-26T09:31:00Z","base":{"ref":"release/10.x"}}]
            """);

        var numbers = await Forge(github).MergedPullRequestsAsync(Target, Sha, "master");

        numbers.Should().Equal(203);
    }

    [Fact]
    public async Task A_commit_the_forge_does_not_know_has_none()
    {
        var github = new StubGitHub().On(HttpMethod.Get, $"{Repo}/commits/{Sha}/pulls?per_page=10", HttpStatusCode.UnprocessableEntity,
            """{"message":"No commit found for SHA"}""");

        (await Forge(github).MergedPullRequestsAsync(Target, Sha, "master")).Should().BeEmpty();
    }
}
