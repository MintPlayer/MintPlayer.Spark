using System.Net;
using CodeCoverage.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MintPlayer.Spark.Authorization.Identity;
using Raven.Client.Documents.Session;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// Who manages an owner on GitHub (dependency-updates PRD D-O12, owner decision R39): the viewer's own account, or an
/// organization GitHub says they own (<c>GET /user/memberships/orgs/{org}</c>: <c>role admin</c>, <c>state active</c>).
/// Reaching the installation (<c>GET /user/installations</c>, which read access to one repository gives) is not enough.
/// </summary>
/// <remarks>
/// The owner's answer is the REAL capture in <c>Fixtures/GitHub/Memberships</c> (see its SOURCE.md); the member and pending
/// answers are that capture with one field changed, as GitHub documents them.
/// </remarks>
public class GitHubOrganizationOwnerTests : CoverageRavenTest
{
    private const string OwnLogin = "acme-owner";
    private const string UserJson = """{ "id": 1001, "login": "acme-owner", "type": "User" }""";

    /// <summary>The viewer reaches acme (an organization) and widget-person (somebody else's personal account).</summary>
    private const string InstallationsJson = """
        {
          "total_count": 2,
          "installations": [
            { "id": 5555, "target_type": "Organization", "suspended_at": null, "account": { "login": "acme", "id": 5, "type": "Organization" } },
            { "id": 6666, "target_type": "User", "suspended_at": null, "account": { "login": "widget-person", "id": 77, "type": "User" } }
          ]
        }
        """;

    private const string MembershipPath = "/user/memberships/orgs/acme";

    private static string OwnerMembership => Fixture.Read("GitHub/Memberships/membership-admin-active.json");
    private static string NotAMember => Fixture.Read("GitHub/Memberships/membership-not-a-member-404.json");

    private sealed record Harness(IGitHubAccessService Access, StubHttpMessageHandler Handler)
    {
        public int MembershipCalls(string path = MembershipPath) => Handler.Requests.Count(r => r.Uri!.AbsolutePath == path);
    }

    private Harness Create(IAsyncDocumentSession session, Func<HttpRequestMessage, HttpResponseMessage> membership)
    {
        var user = new SparkUser
        {
            Id = $"SparkUsers/{Guid.NewGuid():N}",
            UserName = "SomeoneElsesOrg",
            Logins = [new SparkUserLogin { LoginProvider = "GitHub", ProviderKey = "1001" }],
        };
        var handler = new StubHttpMessageHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/user" => StubHttpMessageHandler.Json(HttpStatusCode.OK, UserJson),
            "/user/installations" => StubHttpMessageHandler.Json(HttpStatusCode.OK, InstallationsJson),
            var path when path.StartsWith("/user/memberships/orgs/", StringComparison.Ordinal) => membership(request),
            var path => throw new InvalidOperationException($"unexpected GitHub call {path}"),
        });

        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new FakeHttpContextAccessor(GitHubAuthTestFakes.PrincipalFor(user)));
        services.AddSingleton<MintPlayer.Spark.Abstractions.Authentication.ISparkCurrentUser>(new FakeSparkCurrentUser(user.Id));
        services.AddSingleton(GitHubAuthTestFakes.UserManagerOver(new InMemoryUserStore().Add(user)));
        services.AddSingleton<IGitHubUserTokenService>(new ScriptedTokenService(_ => new GitHubUserToken("user-token", GitHubTokenState.Ok)));
        services.AddSingleton<IHttpClientFactory>(new SingleClientHttpFactory(handler));
        services.AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()));
        services.AddSingleton(session);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScoped<IGitHubAccessService, GitHubAccessService>();
        return new(services.BuildServiceProvider().GetRequiredService<IGitHubAccessService>(), handler);
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Answer(HttpStatusCode status, string body)
        => _ => StubHttpMessageHandler.Json(status, body);

    private static string WithField(string json, string field, string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node[field] = value;
        return node.ToJsonString();
    }

    [Fact]
    public async Task An_active_owner_of_the_organization_manages_it()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var harness = Create(session, Answer(HttpStatusCode.OK, OwnerMembership));

        (await harness.Access.ManagesOwnerAsync("acme")).Should().BeTrue("the real capture: role admin, state active");
        (await harness.Access.ManagesOwnerAsync("ACME")).Should().BeTrue("logins compare ignoring case, and the answer is cached");

        harness.MembershipCalls().Should().Be(1, "cached per (user, org) with the owner set's lifetime");
    }

    [Theory]
    [InlineData("role", "member")]
    [InlineData("role", "billing_manager")]
    [InlineData("state", "pending")]
    public async Task A_member_a_billing_manager_and_a_pending_owner_do_not_manage_it(string field, string value)
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var harness = Create(session, Answer(HttpStatusCode.OK, WithField(OwnerMembership, field, value)));

        (await harness.Access.IsOwnerAllowedAsync("acme")).Should().BeTrue("they reach the installation, so they still SEE acme's repositories");
        (await harness.Access.ManagesOwnerAsync("acme")).Should().BeFalse($"{field} {value} is not an active owner");
    }

    [Fact]
    public async Task Not_a_member_404_does_not_manage_it_and_is_cached()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var harness = Create(session, Answer(HttpStatusCode.NotFound, NotAMember));

        (await harness.Access.ManagesOwnerAsync("acme")).Should().BeFalse("the real 404 GitHub answers a non-member");
        (await harness.Access.ManagesOwnerAsync("acme")).Should().BeFalse();

        harness.MembershipCalls().Should().Be(1, "a 404 is GitHub's definite answer, cached like a yes");
    }

    [Fact]
    public async Task Forbidden_and_failures_do_not_manage_it()
    {
        foreach (var respond in new Func<HttpRequestMessage, HttpResponseMessage>[]
                 {
                     Answer(HttpStatusCode.Forbidden, """{ "message": "Resource not accessible by integration" }"""),
                     Answer(HttpStatusCode.InternalServerError, "{}"),
                     _ =>
                     {
                         var limited = StubHttpMessageHandler.Json(HttpStatusCode.Forbidden, """{ "message": "API rate limit exceeded" }""");
                         limited.Headers.Add("x-ratelimit-remaining", "0");
                         return limited;
                     },
                     _ => throw new HttpRequestException("GitHub is unreachable"),
                     Answer(HttpStatusCode.OK, "not json"),
                 })
        {
            using var store = GetDocumentStore();
            using var session = store.OpenAsyncSession();
            var harness = Create(session, respond);

            (await harness.Access.ManagesOwnerAsync("acme")).Should().BeFalse("fail closed");
        }
    }

    [Fact]
    public async Task A_resync_drops_the_cached_answer()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var owner = true;
        var harness = Create(session, _ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            owner ? OwnerMembership : WithField(OwnerMembership, "role", "member")));

        (await harness.Access.ManagesOwnerAsync("acme")).Should().BeTrue();
        owner = false;
        (await harness.Access.ManagesOwnerAsync("acme")).Should().BeTrue("still cached");
        await harness.Access.InvalidateAsync();
        (await harness.Access.ManagesOwnerAsync("acme")).Should().BeFalse("the resync asks GitHub again: a demoted owner stops managing at once");

        harness.MembershipCalls().Should().Be(2);
    }

    [Fact]
    public async Task The_viewers_own_account_is_managed_without_asking_and_nobody_elses_personal_account_is()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var harness = Create(session, Answer(HttpStatusCode.NotFound, NotAMember));

        (await harness.Access.ManagesOwnerAsync(OwnLogin)).Should().BeTrue("a personal account is managed by that user");
        (await harness.Access.ManagesOwnerAsync("widget-person")).Should().BeFalse(
            "another user's personal account the viewer reaches (a collaborator) is not theirs: the membership endpoint 404s");

        harness.MembershipCalls($"/user/memberships/orgs/{OwnLogin}").Should().Be(0);
    }

    [Fact]
    public async Task An_owner_outside_the_owner_set_costs_no_call()
    {
        using var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        var harness = Create(session, Answer(HttpStatusCode.OK, OwnerMembership));

        (await harness.Access.ManagesOwnerAsync("someone-elses-org")).Should().BeFalse();

        harness.Handler.Requests.Should().NotContain(r => r.Uri!.AbsolutePath.StartsWith("/user/memberships/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "role": "admin", "state": "active" }""", true)]
    [InlineData("""{ "role": "admin", "state": "pending" }""", false)]
    [InlineData("""{ "role": "member", "state": "active" }""", false)]
    [InlineData("""{ "state": "active" }""", false)]
    [InlineData("""{ "role": 1, "state": "active" }""", false)]
    [InlineData("""[]""", false)]
    [InlineData("""{""", false)]
    public void The_membership_body_is_an_owners_only_for_role_admin_and_state_active(string json, bool expected)
        => GitHubAccessService.IsActiveOwnerMembership(json).Should().Be(expected);
}
