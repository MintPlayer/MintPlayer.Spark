using System.Net;
using System.Text.Json;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The App's reactions on a command comment (#660 M2), against GitHub's documented endpoints and the statuses spike S1
/// measured: 201 new, 200 the existing one, 204 removed, 404 once gone.
/// </summary>
public class GitHubForgeCommentReactionsTests
{
    private const string Reactions = "/repos/acme/widget/issues/comments/900/reactions";
    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 77);

    private static Repository Target() => new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, 910040),
        GitHubId = 910040, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme", Account = AccountId,
    };

    private static GitHubForgeCommentReactions Forge(StubGitHub github)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Account>(AccountId, Arg.Any<CancellationToken>()).Returns(new Account { GitHubId = 77, Login = "acme", InstallationId = 4242 });
        return new GitHubForgeCommentReactions(github, new SingleClientHttpFactory(github.Rest), session);
    }

    [Theory]
    [InlineData(EForgeReaction.Eyes, "eyes")]
    [InlineData(EForgeReaction.Rocket, "rocket")]
    [InlineData(EForgeReaction.Hooray, "hooray")]
    [InlineData(EForgeReaction.ThumbsUp, "+1")]
    [InlineData(EForgeReaction.ThumbsDown, "-1")]
    public async Task A_reaction_is_posted_with_its_content_and_its_id_returned(EForgeReaction reaction, string content)
    {
        string? posted = null;
        var github = new StubGitHub().On(HttpMethod.Post, Reactions, (_, body) =>
        {
            posted = body;
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, $$$"""{"id":31,"content":"{{{content}}}","user":{"login":"bot","id":1}}""");
        });

        (await Forge(github).AddAsync(Target(), 900, reaction)).Should().Be(31);

        using var request = JsonDocument.Parse(posted!);
        request.RootElement.GetProperty("content").GetString().Should().Be(content);
    }

    [Fact]
    public async Task A_reaction_already_there_returns_its_existing_id()
    {
        var github = new StubGitHub().On(HttpMethod.Post, Reactions, HttpStatusCode.OK, """{"id":31,"content":"eyes"}""");

        (await Forge(github).AddAsync(Target(), 900, EForgeReaction.Eyes)).Should().Be(31);
    }

    [Fact]
    public async Task A_refused_reaction_names_the_permission()
    {
        var github = new StubGitHub().On(HttpMethod.Post, Reactions, HttpStatusCode.Forbidden, """{"message":"Resource not accessible by integration"}""");

        var act = () => Forge(github).AddAsync(Target(), 900, EForgeReaction.Eyes);

        (await act.Should().ThrowAsync<ForgeAccessDeniedException>()).Which.RequiredPermission.Should().Be("issues=write");
    }

    [Fact]
    public async Task A_reaction_is_removed_by_its_id()
    {
        var github = new StubGitHub().On(HttpMethod.Delete, $"{Reactions}/31", HttpStatusCode.NoContent, "");

        await Forge(github).RemoveAsync(Target(), 900, 31);

        github.Calls.Should().Contain(c => c.Contains($"{Reactions}/31"));
    }

    [Fact]
    public async Task Removing_a_reaction_that_is_gone_is_no_error()
    {
        var github = new StubGitHub().On(HttpMethod.Delete, $"{Reactions}/31", HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        await Forge(github).RemoveAsync(Target(), 900, 31);
    }

    [Fact]
    public async Task A_failed_removal_throws()
    {
        var github = new StubGitHub().On(HttpMethod.Delete, $"{Reactions}/31", HttpStatusCode.InternalServerError, """{"message":"boom"}""");

        var act = () => Forge(github).RemoveAsync(Target(), 900, 31);

        await act.Should().ThrowAsync<ForgeRequestException>();
    }
}
