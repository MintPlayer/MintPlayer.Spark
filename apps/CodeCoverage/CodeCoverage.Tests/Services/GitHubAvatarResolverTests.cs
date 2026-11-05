using System.Text.Json;
using CodeCoverage.CommitGraph;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>The commit graph's GitHub avatars (D28 step 4): one aliased GraphQL request per 100 authors, values as variables.</summary>
public class GitHubAvatarResolverTests
{
    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 77);

    private static Repository Target(string fullName = "acme/widget", bool installed = true) => new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, 910040), GitHubId = 910040, Name = "widget", FullName = fullName,
        OwnerLogin = "acme", Account = installed ? AccountId : null,
    };

    private static GitHubAvatarResolver Resolver(StubGitHub github, long? installationId = 4242)
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Account>(AccountId, Arg.Any<CancellationToken>()).Returns(new Account { GitHubId = 77, Login = "acme", InstallationId = installationId });
        return new GitHubAvatarResolver(session, github, NullLogger<GitHubAvatarResolver>.Instance);
    }

    private static string Sha(int i) => i.ToString("x40");

    private static AvatarLookup Lookup(int i) => new($"dev{i}@example.invalid", Sha(i));

    [Fact]
    public async Task Each_author_gets_the_avatar_github_returns_or_one_built_from_its_id()
    {
        var github = new StubGitHub().OnGraphQL("""
            {"data":{"repository":{
              "c0":{"author":{"user":{"databaseId":1,"login":"octocat","avatarUrl":"https://avatars.githubusercontent.com/u/1?v=4"}}},
              "c1":{"author":{"user":{"databaseId":49699333,"login":"dependabot[bot]"}}},
              "c2":{"author":{"user":null}},
              "c3":null}}}
            """);

        var avatars = await Resolver(github).ResolveAsync(Target(), [Lookup(0), Lookup(1), Lookup(2), Lookup(3)]);

        avatars.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["dev0@example.invalid"] = "https://avatars.githubusercontent.com/u/1?v=4",
            ["dev1@example.invalid"] = "https://avatars.githubusercontent.com/u/49699333?v=4",
            ["dev2@example.invalid"] = null,
            ["dev3@example.invalid"] = null,
        });
    }

    [Fact]
    public async Task The_query_is_built_from_indices_and_every_value_is_a_variable()
    {
        var github = new StubGitHub().OnGraphQL("""{"data":{"repository":{}}}""");

        await Resolver(github).ResolveAsync(Target(), [Lookup(7)]);

        using var body = JsonDocument.Parse(github.GraphQLBodies.Single());
        var query = body.RootElement.GetProperty("query").GetString()!;
        var variables = body.RootElement.GetProperty("variables");
        query.Should().Contain("c0: object(oid: $o0)").And.NotContain("acme").And.NotContain(Sha(7));
        (variables.GetProperty("owner").GetString(), variables.GetProperty("name").GetString(), variables.GetProperty("o0").GetString())
            .Should().Be(("acme", "widget", Sha(7)));
    }

    [Fact]
    public async Task More_than_a_hundred_authors_take_a_second_request()
    {
        var github = new StubGitHub().OnGraphQL("""{"data":{"repository":{}}}""").OnGraphQL("""{"data":{"repository":{}}}""");

        var avatars = await Resolver(github).ResolveAsync(Target(), [.. Enumerable.Range(0, 150).Select(Lookup)]);

        github.GraphQLBodies.Should().HaveCount(2);
        avatars!.Should().HaveCount(150);
    }

    [Fact]
    public async Task Lookups_without_a_full_sha_ask_nothing()
    {
        var github = new StubGitHub();

        var avatars = await Resolver(github).ResolveAsync(Target(), [new AvatarLookup("a@example.invalid", "abc123"), new AvatarLookup("b@example.invalid", new string('A', 40))]);

        avatars.Should().BeEmpty();
        github.GraphQLBodies.Should().BeEmpty();
    }

    public static TheoryData<string, bool, long?> Unresolvable => new()
    {
        { "acme/widget", false, 4242 }, // no account
        { "acme/widget", true, null },  // an account without the App
        { "widget", true, 4242 },       // a full name without an owner
    };

    [Theory]
    [MemberData(nameof(Unresolvable))]
    public async Task A_repository_without_an_installation_resolves_nobody(string fullName, bool installed, long? installationId)
    {
        var github = new StubGitHub();

        (await Resolver(github, installationId).ResolveAsync(Target(fullName, installed), [Lookup(0)])).Should().BeNull();
        github.GraphQLBodies.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"data":{"repository":null},"errors":[{"type":"NOT_FOUND","message":"Could not resolve to a Repository"}]}""")]
    [InlineData("""{"errors":[{"type":"FORBIDDEN","message":"Resource not accessible by integration"}]}""")]
    public async Task An_unreadable_repository_resolves_nobody(string answer)
    {
        var github = new StubGitHub().OnGraphQL(answer);

        (await Resolver(github).ResolveAsync(Target(), [Lookup(0)])).Should().BeNull();
    }

    [Fact]
    public async Task A_broken_app_credential_falls_back_to_initials()
    {
        var github = new StubGitHub { Throws = new InvalidOperationException("The App's private key is invalid.") };

        (await Resolver(github).ResolveAsync(Target(), [Lookup(0)])).Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_resolution_is_not_swallowed()
    {
        var github = new StubGitHub { Throws = new OperationCanceledException() };

        await ((Func<Task>)(() => Resolver(github).ResolveAsync(Target(), [Lookup(0)]))).Should().ThrowAsync<OperationCanceledException>();
    }
}
