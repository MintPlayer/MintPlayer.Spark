using System.Net;
using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Messaging.Abstractions;
using NSubstitute;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// P16 / R37: a rate limit is retried and never mistaken for data or for a refusal, classified on the REAL captures in
/// <c>Fixtures/GitHub/RateLimits</c> (see its SOURCE.md: the primary limit and the permission 403s are live captures, the
/// secondary limit is GitHub's documented body from a real third-party log).
/// </summary>
public class GitHubRateLimitTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1792178800);

    private static ForgeRateLimitedException? Classify(string fixture)
    {
        using var response = Fixture.Http($"GitHub/RateLimits/{fixture}");
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var json = System.Text.Json.JsonDocument.Parse(body);
        var message = json.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        return GitHubRateLimits.Classify(response, message, Now, "GET x");
    }

    [Theory]
    [InlineData("a1-primary-exhausted-rest.http")]
    [InlineData("a2-primary-exhausted-trees.http")]
    public void The_primary_limit_waits_until_its_reset(string fixture)
    {
        var limited = Classify(fixture)!;

        limited.Should().NotBeNull();
        limited.Kind.Should().Be(EForgeRateLimitKind.Primary);
        limited.ResetsAtUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792178848));
        limited.Delay.Should().Be(TimeSpan.FromSeconds(48) + GitHubRateLimits.ResetSkew);
    }

    [Fact]
    public void The_secondary_limit_is_recognised_by_its_message_and_waits_at_least_a_minute()
    {
        var limited = Classify("c1-secondary-search-no-retry-after.http")!;

        limited.Should().NotBeNull("x-ratelimit-remaining is 30 there: only the message tells it apart from a refusal");
        limited.Kind.Should().Be(EForgeRateLimitKind.Secondary);
        limited.Delay.Should().Be(GitHubRateLimits.SecondaryMinimum);
    }

    [Theory]
    [InlineData("b1-collaborators-no-push.http")]
    [InlineData("b2-org-admin-endpoint.http")]
    public void A_permission_403_is_not_a_rate_limit(string fixture)
        => Classify(fixture).Should().BeNull();

    [Fact]
    public void Retry_after_is_honoured_and_a_bare_429_waits_a_minute()
    {
        var withHint = GitHubRateLimits.Classify(HttpStatusCode.Forbidden, name => name == "retry-after" ? "30" : name == "x-ratelimit-remaining" ? "12" : null,
            "You have exceeded a secondary rate limit.", Now, "POST x");
        var bare = GitHubRateLimits.Classify(HttpStatusCode.TooManyRequests, _ => null, null, Now, "POST x");

        withHint!.Delay.Should().Be(TimeSpan.FromSeconds(30));
        withHint.ResetsAtUtc.Should().Be(Now.AddSeconds(30));
        bare!.Delay.Should().Be(GitHubRateLimits.SecondaryMinimum);
        GitHubRateLimits.Classify(HttpStatusCode.NotFound, _ => null, "rate limit", Now, "GET x").Should().BeNull("only 403 and 429 are limits");
    }

    [Fact]
    public void A_graphql_rate_limit_is_an_error_type_on_a_200()
    {
        GitHubRateLimits.FromGraphQLErrors([("RATE_LIMITED", "API rate limit exceeded")], _ => null, Now, "q")!.Kind.Should().Be(EForgeRateLimitKind.GraphQL);
        GitHubRateLimits.FromGraphQLErrors([("FORBIDDEN", "Resource not accessible by integration")], _ => null, Now, "q").Should().BeNull();
    }

    /// <summary>D-O1: the adapters keep answering ForgeRequestException(429), and the message bus, which looks one level in,
    /// finds the RetryAfterException there and parks the handler instead of charging it an attempt.</summary>
    [Fact]
    public void The_wrapper_carries_the_retry_after_one_level_in()
    {
        var limited = Classify("a1-primary-exhausted-rest.http")!;
        var thrown = new ForgeRequestException("x", 429, limited);

        thrown.InnerException.Should().BeAssignableTo<RetryAfterException>();
        ForgeRateLimitedException.Find(thrown).Should().BeSameAs(limited);
        ForgeRateLimitedException.Find(new ForgeRequestException("x", 500)).Should().BeNull();
    }

    // ── The adapters, on StubGitHub ──────────────────────────────────────────────────────────────

    private const long InstallationId = 4242;
    private static readonly string AccountId = Account.DocumentId(EForgeProvider.GitHub, 77);

    private static Repository Target(bool isPrivate = true) => new()
    {
        Id = Repository.DocumentId(EForgeProvider.GitHub, 910040), GitHubId = 910040, Name = "widget", FullName = "acme/widget", OwnerLogin = "acme",
        Account = AccountId, IsPrivate = isPrivate, DefaultBranch = "main",
    };

    private static IAsyncDocumentSession Session()
    {
        var session = Substitute.For<IAsyncDocumentSession>();
        session.LoadAsync<Account>(AccountId, Arg.Any<CancellationToken>()).Returns(new Account { GitHubId = 77, Login = "acme", InstallationId = InstallationId });
        return session;
    }

    private static GitHubForgeClient Client(StubGitHub github) => new(
        new GitHubDiffService(github, new SingleClientHttpFactory(github.Rest), NullLogger<GitHubDiffService>.Instance),
        new GitHubContentService(github, new SingleClientHttpFactory(github.Rest), new SourceContentCache(), NullLogger<GitHubContentService>.Instance),
        Session(), github, NullLogger<GitHubForgeClient>.Instance);

    private static StubGitHub Serving(string path, string fixture)
        => new StubGitHub().On(HttpMethod.Get, path, (_, _) => Fixture.Http($"GitHub/RateLimits/{fixture}"));

    [Fact]
    public async Task A_throttled_tree_read_throws_instead_of_reading_as_no_tree()
    {
        var github = Serving("/repositories/910040/branches/main", "a2-primary-exhausted-trees.http");

        var act = () => Client(github).GetTreeAsync(Target(), "main");

        var thrown = (await act.Should().ThrowAsync<ForgeRequestException>()).Which;
        thrown.StatusCode.Should().Be(429);
        ForgeRateLimitedException.Find(thrown)!.Kind.Should().Be(EForgeRateLimitKind.Primary);
    }

    [Fact]
    public async Task A_missing_branch_is_still_no_tree()
        => (await Client(new StubGitHub()).GetTreeAsync(Target(), "main")).Should().BeNull();

    [Fact]
    public async Task A_refused_tree_read_is_a_refusal_not_an_absence()
    {
        var github = Serving("/repositories/910040/branches/main", "b1-collaborators-no-push.http");

        await ((Func<Task>)(() => Client(github).GetTreeAsync(Target(), "main"))).Should().ThrowAsync<ForgeAccessDeniedException>();
    }

    [Fact]
    public async Task A_throttled_file_read_throws_instead_of_reading_as_no_file()
    {
        var github = Serving("/repositories/910040/contents/coverage.yml", "a1-primary-exhausted-rest.http");

        var act = () => Client(github).GetFileContentAsync(Target(), "main", "coverage.yml");

        ForgeRateLimitedException.Find((await act.Should().ThrowAsync<ForgeRequestException>()).Which).Should().NotBeNull();
    }

    [Fact]
    public async Task A_secondary_limit_on_a_dependency_update_call_is_not_a_permission_problem()
    {
        var github = Serving("/repos/acme/widget/pulls/41", "c1-secondary-search-no-retry-after.http");
        var updates = new GitHubForgeDependencyUpdates(github, new SingleClientHttpFactory(github.Rest), Session());

        var act = () => updates.GetPullRequestAsync(Target(), 41);

        var thrown = (await act.Should().ThrowAsync<ForgeRequestException>()).Which;
        ForgeRateLimitedException.Find(thrown)!.Kind.Should().Be(EForgeRateLimitKind.Secondary);
    }

    [Fact]
    public async Task A_permission_403_on_a_dependency_update_call_stays_a_refusal()
    {
        var github = Serving("/repos/acme/widget/pulls/41", "b1-collaborators-no-push.http");
        var updates = new GitHubForgeDependencyUpdates(github, new SingleClientHttpFactory(github.Rest), Session());

        await ((Func<Task>)(() => updates.GetPullRequestAsync(Target(), 41))).Should().ThrowAsync<ForgeAccessDeniedException>();
    }
}
