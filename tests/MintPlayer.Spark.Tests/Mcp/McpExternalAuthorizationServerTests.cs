using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// Setup 2 end to end (MCP PRD D4, D22, §6.2 "External setup"; plan M3c): an external authorization server's access
/// tokens on <c>/mcp</c>, mapped to a local user only through a linked login of the bound provider, the shadow grant,
/// the token scope as a write cap, local groups, and revocation. Real Identity, real RavenDB, the real Mcp gate; the
/// external server is <see cref="TestIssuer"/> (in-memory keys).
/// </summary>
public class McpExternalAuthorizationServerTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private readonly TestIssuer issuer = new();

    private string Name(string localPart) => $"{localPart}-{Scope}";

    /// <summary>A host and a user (in Developers unless told otherwise) whose <see cref="McpExternalHost.Provider"/> login is <c>subject</c>.</summary>
    private async Task<(SparkEndpointFactory<GuardedContext> Factory, string UserId, string Subject)> StartAsync(
        string user, bool developer = true, bool link = true)
    {
        var factory = McpExternalHost.Create(Store, issuer);
        var created = await McpAccountHost.CreateUserAsync(factory, Name(user), developer);
        var subject = "corp-" + Name(user);
        if (link)
            await McpExternalHost.LinkAsync(factory, Store, created.Id!, McpExternalHost.Provider, subject);
        return (factory, created.Id!, subject);
    }

    private static Task<Reply> WhoAmIAsync(HttpClient client, string token)
        => CallAsync(client, "acme_whoami", new { }, McpExternalHost.Token(token));

    private static Task<Reply> DescribeAsync(HttpClient client, string token)
        => CallAsync(client, "acme_describe", new { type = "GuardedDoc" }, McpExternalHost.Token(token));

    private string GrantId(string userId) => AgentGrantIds.ExternalAuthorizationServer(userId, issuer.Issuer, TestIssuer.ClientId);

    // ── the mapping ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_token_of_a_linked_login_calls_mcp_as_the_local_user_with_the_users_groups_from_the_store()
    {
        var (factory, userId, subject) = await StartAsync("linked");
        await using var _ = factory;
        var client = factory.CreateClient();
        var token = issuer.Token(subject);

        var whoami = await WhoAmIAsync(client, token);
        var describe = await DescribeAsync(client, token);

        whoami.Status.Should().Be(HttpStatusCode.OK, whoami.Body?.ToJsonString());
        whoami.ToolText.Should().Contain($"authenticated=True;id={userId};scheme={ExternalAgentServer.Scheme}",
            "ISparkCurrentUser.Id is the local id, never the external subject");
        describe.IsToolError.Should().BeFalse(describe.ToolText + " — Developers must come from the local user");
    }

    [Fact]
    public async Task An_unlinked_subject_is_a_401_not_anonymous()
    {
        var (factory, _, _) = await StartAsync("unlinked", link: false);
        await using var _f = factory;

        var reply = await ListToolsAsync(factory.CreateClient(), McpExternalHost.Token(issuer.Token("corp-" + Name("unlinked"))));

        reply.Status.Should().Be(HttpStatusCode.Unauthorized);
        reply.Response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
    }

    [Fact]
    public async Task An_account_with_the_same_email_but_no_linked_login_is_not_mapped()
    {
        var (factory, _, _) = await StartAsync("email", link: false);
        await using var _f = factory;
        var email = Name("email") + "@test.local";

        var token = issuer.Token("corp-someone-else-" + Scope, claims: new Dictionary<string, object>
        {
            ["email"] = email,
            ["email_verified"] = true,
            ["preferred_username"] = Name("email"),
        });
        var reply = await WhoAmIAsync(factory.CreateClient(), token);

        reply.Status.Should().Be(HttpStatusCode.Unauthorized, "a verified email equal to a local account's maps nothing (D22)");
    }

    [Fact]
    public async Task A_subject_linked_under_another_provider_is_not_mapped()
    {
        var (factory, userId, _) = await StartAsync("other", link: false);
        await using var _f = factory;
        var subject = "shared-" + Name("other");
        await McpExternalHost.LinkAsync(factory, Store, userId, McpExternalHost.OtherProvider, subject);

        var reply = await WhoAmIAsync(factory.CreateClient(), issuer.Token(subject));

        reply.Status.Should().Be(HttpStatusCode.Unauthorized, "the issuer is bound to Corp; a Partner login with the same key is someone else's account");
    }

    [Fact]
    public async Task Token_role_and_group_claims_grant_nothing()
    {
        var (factory, _, subject) = await StartAsync("roles", developer: false);
        await using var _f = factory;
        var token = issuer.Token(subject, claims: new Dictionary<string, object>
        {
            ["role"] = McpAccountHost.Developers,
            ["groups"] = new[] { McpAccountHost.Developers },
            ["http://schemas.microsoft.com/ws/2008/06/identity/claims/role"] = McpAccountHost.Developers,
        });

        var describe = await DescribeAsync(factory.CreateClient(), token);

        describe.Status.Should().Be(HttpStatusCode.OK, "the token itself is valid and mapped");
        describe.ToolText.Should().Be(SparkMcpResult.NotFoundMessage, "groups are the local user's, never the token's");
    }

    // ── the token ───────────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> RefusedTokens => new()
    {
        "another issuer, same key",
        "another issuer, its own key",
        "the right issuer name, another key",
        "another audience",
        "no audience",
        "the website's client as audience (an ID token)",
        "typed JWT (an ID token)",
        "expired",
        "not yet valid",
        "alg none",
        "HMAC",
        "sender-constrained",
        "no client",
    };

    [Theory]
    [MemberData(nameof(RefusedTokens))]
    public async Task A_token_that_is_not_for_this_endpoint_is_a_401(string variant)
    {
        var (factory, _, subject) = await StartAsync("bad-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(variant)))[..8]);
        await using var _f = factory;
        var other = new TestIssuer("https://evil.example");
        var now = DateTime.UtcNow;

        var token = variant switch
        {
            "another issuer, same key" => issuer.Token(subject, issuer: other.Issuer),
            "another issuer, its own key" => other.Token(subject),
            "the right issuer name, another key" => issuer.Token(subject, key: other.Key),
            "another audience" => issuer.Token(subject, audience: "https://other-api.example"),
            "no audience" => issuer.Token(subject, audience: null),
            "the website's client as audience (an ID token)" => issuer.Token(subject, audience: "website", type: "JWT"),
            "typed JWT (an ID token)" => issuer.Token(subject, type: "JWT"),
            "expired" => issuer.Token(subject, notBefore: now.AddHours(-2), expires: now.AddHours(-1)),
            "not yet valid" => issuer.Token(subject, notBefore: now.AddHours(1), expires: now.AddHours(2)),
            "alg none" => issuer.UnsignedToken(subject),
            "HMAC" => issuer.Token(subject, key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), algorithm: SecurityAlgorithms.HmacSha256),
            "sender-constrained" => issuer.Token(subject, claims: new Dictionary<string, object> { ["cnf"] = new Dictionary<string, object> { ["jkt"] = "abc" } }),
            "no client" => issuer.Token(subject, clientId: ""),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
        var reply = await WhoAmIAsync(factory.CreateClient(), token);

        reply.Status.Should().Be(HttpStatusCode.Unauthorized, variant);
    }

    [Fact]
    public async Task The_protected_resource_metadata_points_at_the_external_server()
    {
        var (factory, _, _) = await StartAsync("prm", link: false);
        await using var _f = factory;

        var document = (await factory.CreateClient().GetFromJsonAsync<JsonObject>("/.well-known/oauth-protected-resource/mcp"))!;

        document["resource"]!.GetValue<string>().Should().Be(McpTestHost.CanonicalUrl);
        document["authorization_servers"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Equal(issuer.Issuer);
        document["scopes_supported"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Equal("mcp:read", "mcp:write");
    }

    [Fact]
    public async Task An_opaque_token_is_introspected_and_mapped_the_same_way()
    {
        var stub = new IntrospectionStub();
        stub.Install();
        await using var factory = McpExternalHost.Create(Store, new TestIssuer(IntrospectionStub.Issuer), introspection: stub);
        var user = await McpAccountHost.CreateUserAsync(factory, Name("opaque"));
        await McpExternalHost.LinkAsync(factory, Store, user.Id!, McpExternalHost.Provider, "corp-" + Name("opaque"));
        var client = factory.CreateClient();

        var mapped = await WhoAmIAsync(client, stub.Issue("corp-" + Name("opaque")));
        var otherAudience = await WhoAmIAsync(client, stub.Issue("corp-" + Name("opaque"), audience: "https://other-api.example"));
        var otherIssuer = await WhoAmIAsync(client, stub.Issue("corp-" + Name("opaque"), issuer: "https://evil.example"));
        var unknown = await WhoAmIAsync(client, "opaque-never-issued");

        mapped.ToolText.Should().Contain($"authenticated=True;id={user.Id};");
        otherAudience.Status.Should().Be(HttpStatusCode.Unauthorized);
        otherIssuer.Status.Should().Be(HttpStatusCode.Unauthorized);
        unknown.Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── the shadow grant ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_call_creates_a_read_only_grant_for_everything_exposed_listed_with_its_issuer()
    {
        var (factory, userId, subject) = await StartAsync("shadow");
        await using var _f = factory;
        (await WhoAmIAsync(factory.CreateClient(), issuer.Token(subject, scope: "mcp:read mcp:write"))).Status.Should().Be(HttpStatusCode.OK);

        using (var session = Store.OpenAsyncSession())
        {
            var grant = await session.LoadAsync<AgentGrant>(GrantId(userId));
            grant.Should().NotBeNull("the grant is created on first use (D22)");
            grant!.Kind.Should().Be(EAgentKind.ExternalAuthorizationServer);
            grant.Permissions.Should().Be(EAgentPermission.Read, "read only, even though the token asked for mcp:write (Q18)");
            grant.Types.Should().BeNull("everything exposed");
            grant.ClientId.Should().Be(TestIssuer.ClientId);
            grant.Issuer.Should().Be(issuer.Issuer);
            grant.VerifiedHost.Should().BeNull();
        }

        var browser = await McpBrowser.SignInAsync(factory.CreateClient(), Name("shadow"));
        var listed = (await browser.AgentsAsync())["grants"]!.AsArray().Single()!;
        listed["kind"]!.GetValue<string>().Should().Be("ExternalAuthorizationServer");
        listed["issuer"]!.GetValue<string>().Should().Be(issuer.Issuer);
        listed["clientId"]!.GetValue<string>().Should().Be(TestIssuer.ClientId);
        listed["status"]!.GetValue<string>().Should().Be("active");
    }

    [Fact]
    public async Task Changes_need_the_users_toggle_even_with_mcp_write_in_the_token()
    {
        var (factory, _, subject) = await StartAsync("toggle");
        await using var _f = factory;
        var client = factory.CreateClient();
        var token = issuer.Token(subject, scope: "mcp:read mcp:write");

        var tools = await ListToolsAsync(client, McpExternalHost.Token(token));
        var touch = await CallAsync(client, "acme_touch", new { }, McpExternalHost.Token(token));

        tools.Status.Should().Be(HttpStatusCode.OK);
        tools.ToolNames.Should().Contain("acme_whoami").And.NotContain("acme_touch", "the shadow grant is read only");
        touch.ToolText.Should().NotBe("touched");
    }

    [Fact]
    public async Task Changes_need_mcp_write_in_the_token_even_with_the_users_toggle()
    {
        var (factory, _, subject) = await StartAsync("scope");
        await using var _f = factory;
        var client = factory.CreateClient();
        var readOnly = issuer.Token(subject, scope: "mcp:read");
        await WhoAmIAsync(client, readOnly); // creates the grant

        var browser = await McpBrowser.SignInAsync(factory.CreateClient(), Name("scope"));
        var key = (await browser.AgentsAsync())["grants"]!.AsArray().Single()!["key"]!.GetValue<string>();
        using var update = await browser.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/update",
            new { key, permissions = "Read, Write", types = (object?)null });
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        var withoutScope = await ListToolsAsync(client, McpExternalHost.Token(readOnly));
        var touchWithoutScope = await CallAsync(client, "acme_touch", new { }, McpExternalHost.Token(readOnly));
        var withScope = issuer.Token(subject, scope: "mcp:read mcp:write");
        var touchWithScope = await CallAsync(client, "acme_touch", new { }, McpExternalHost.Token(withScope));

        withoutScope.ToolNames.Should().NotContain("acme_touch", "in this setup only, the token's scope is a real cap (D22)");
        touchWithoutScope.ToolText.Should().NotBe("touched");
        (await ListToolsAsync(client, McpExternalHost.Token(withScope))).ToolNames.Should().Contain("acme_touch");
        touchWithScope.ToolText.Should().Be("touched", "toggle and scope together allow changes");
    }

    [Fact]
    public void The_write_cap_is_the_intersection_of_the_grant_and_the_token_scope()
    {
        var all = new AgentGrant { Id = "AgentGrants/u/ext/x", Permissions = EAgentPermission.Read | EAgentPermission.Write };
        var typed = new AgentGrant
        {
            Id = "AgentGrants/u/ext/y",
            Permissions = EAgentPermission.Read | EAgentPermission.Write,
            Types = [new AgentGrantType { Name = "GuardedDoc", Permissions = EAgentPermission.Read | EAgentPermission.Write }],
        };
        var readOnly = new AgentGrant { Id = "AgentGrants/u/ext/z", Permissions = EAgentPermission.Read };

        ExternalAgentServerScheme.ScopeOf(all, ["mcp:read", "mcp:write"], "mcp:write").Permissions.Should().Be(EAgentPermission.Read | EAgentPermission.Write);
        ExternalAgentServerScheme.ScopeOf(all, ["mcp:read"], "mcp:write").Permissions.Should().Be(EAgentPermission.Read);
        ExternalAgentServerScheme.ScopeOf(all, ["MCP:WRITE"], "mcp:write").Permissions.Should().Be(EAgentPermission.Read, "scopes are case-sensitive");
        ExternalAgentServerScheme.ScopeOf(typed, ["mcp:read"], "mcp:write").For("GuardedDoc").Should().Be(EAgentPermission.Read, "the cap reaches every chosen type");
        ExternalAgentServerScheme.ScopeOf(readOnly, ["mcp:write"], "mcp:write").Permissions.Should().Be(EAgentPermission.Read, "the scope never widens the grant");
    }

    [Fact]
    public async Task A_disconnected_grant_refuses_the_next_call_and_is_not_recreated_until_the_user_allows_it_again()
    {
        var (factory, userId, subject) = await StartAsync("revoke");
        await using var _f = factory;
        var client = factory.CreateClient();
        var token = issuer.Token(subject);
        (await WhoAmIAsync(client, token)).Status.Should().Be(HttpStatusCode.OK);

        var browser = await McpBrowser.SignInAsync(factory.CreateClient(), Name("revoke"));
        var key = (await browser.AgentsAsync())["grants"]!.AsArray().Single()!["key"]!.GetValue<string>();
        using var revoke = await browser.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/revoke", new { key });
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        var next = await WhoAmIAsync(client, token);
        var fresh = await WhoAmIAsync(client, issuer.Token(subject));
        next.Status.Should().Be(HttpStatusCode.Unauthorized, "revoked on the server, though the external server still vouches for the token");
        fresh.Status.Should().Be(HttpStatusCode.Unauthorized, "a new token from the external server does not bring the grant back");
        (await browser.AgentsAsync())["grants"]!.AsArray().Single()!["status"]!.GetValue<string>().Should().Be("disconnected");

        using var allow = await browser.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/update", new { key, permissions = "Read", types = (object?)null });
        allow.StatusCode.Should().Be(HttpStatusCode.OK);
        (await WhoAmIAsync(client, token)).ToolText.Should().Contain($"id={userId};", "an edit allows the agent again");
    }

    [Fact]
    public async Task A_security_stamp_change_disconnects_the_agent_until_the_user_allows_it_again_which_stamps_the_grant_anew()
    {
        var (factory, userId, subject) = await StartAsync("stamp");
        await using var _f = factory;
        var client = factory.CreateClient();
        var token = issuer.Token(subject);
        (await WhoAmIAsync(client, token)).Status.Should().Be(HttpStatusCode.OK);
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<AgentGrant>(GrantId(userId)))!.SecurityStamp
                .Should().Be((await session.LoadAsync<SparkUser>(userId))!.SecurityStamp, "the shadow grant is stamped when it is made");
        }

        // What a password change, a removed sign-in method or an administrator's reset does.
        await McpAccountHost.ChangeUserAsync(factory, userId, (users, user) => users.UpdateSecurityStampAsync(user));

        var refused = await WhoAmIAsync(client, token);
        var fresh = await WhoAmIAsync(client, issuer.Token(subject));
        refused.Status.Should().Be(HttpStatusCode.Unauthorized, "the external server still vouches for the token; the stamp no longer matches");
        fresh.Status.Should().Be(HttpStatusCode.Unauthorized, "and a new token from it does not bring the agent back");

        var browser = await McpBrowser.SignInAsync(factory.CreateClient(), Name("stamp"));
        var listed = (await browser.AgentsAsync())["grants"]!.AsArray().Single()!;
        listed["status"]!.GetValue<string>().Should().Be("disconnected", "it counts as disconnected until the user allows it again");

        using var allow = await browser.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/update",
            new { key = listed["key"]!.GetValue<string>(), permissions = "Read", types = (object?)null });
        allow.StatusCode.Should().Be(HttpStatusCode.OK, await allow.Content.ReadAsStringAsync());

        (await WhoAmIAsync(client, token)).ToolText.Should().Contain($"id={userId};", "allowing it again stamps the grant anew");
        using (var session = Store.OpenAsyncSession())
        {
            (await session.LoadAsync<AgentGrant>(GrantId(userId)))!.SecurityStamp
                .Should().Be((await session.LoadAsync<SparkUser>(userId))!.SecurityStamp);
        }
        (await browser.AgentsAsync())["grants"]!.AsArray().Single()!["status"]!.GetValue<string>().Should().Be("active");
    }

    [Fact]
    public async Task Unlinking_the_login_refuses_the_next_call()
    {
        var (factory, userId, subject) = await StartAsync("unlink");
        await using var _f = factory;
        var client = factory.CreateClient();
        var token = issuer.Token(subject);
        (await WhoAmIAsync(client, token)).Status.Should().Be(HttpStatusCode.OK);

        await McpAccountHost.ChangeUserAsync(factory, userId, (users, user) => users.RemoveLoginAsync(user, McpExternalHost.Provider, subject));

        (await WhoAmIAsync(client, token)).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── startup ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_issuer_bound_to_a_provider_that_is_not_registered_refuses_startup()
    {
        var act = () => McpExternalHost.Create(Store, issuer, loginProvider: "Nobody");

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("'Nobody'");
    }

    [Fact]
    public void The_external_setup_without_a_canonical_url_refuses_startup()
    {
        var act = () => McpExternalHost.Create(Store, issuer, configureMcp: mcp => mcp.CanonicalUrl = null);

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("canonical URL");
    }

    [Fact]
    public void An_explicit_external_setup_that_is_not_wired_refuses_startup()
    {
        var act = () => McpAccountHost.Create(Store, mcp => mcp.Authentication = EAgentAuthentication.ExternalAuthorizationServer);

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("ExternalAuthorizationServer");
    }
}
