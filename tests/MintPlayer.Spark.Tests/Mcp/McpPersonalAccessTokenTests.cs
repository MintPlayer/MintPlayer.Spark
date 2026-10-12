using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// Setup 1 end to end (MCP PRD D4, D6, D11–D15, §4.6; plan M3a): personal access tokens created on the account
/// page, their grants narrowing every call, store-backed groups, revocation (deletion, expiry, security stamp,
/// lockout), and the account endpoints' ownership rule. Real Identity, real RavenDB, the real Mcp gate.
/// </summary>
public class McpPersonalAccessTokenTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private string Name(string localPart) => $"{localPart}-{Scope}";

    private async Task<(SparkEndpointFactory<GuardedContext> Factory, McpBrowser Browser, string UserId)> StartAsync(
        string user, Action<SparkMcpOptions>? configure = null, bool developer = true)
    {
        var factory = McpAccountHost.Create(Store, configure);
        var created = await McpAccountHost.CreateUserAsync(factory, Name(user), developer);
        var browser = await McpBrowser.SignInAsync(factory.CreateClient(), Name(user));
        return (factory, browser, created.Id!);
    }

    private static Task<Reply> DescribeAsync(HttpClient client, string type, string token)
        => CallAsync(client, "acme_describe", new { type }, McpBrowser.Token(token));

    // ── the token ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_token_is_shown_once_stored_hashed_and_never_echoed()
    {
        var (factory, browser, userId) = await StartAsync("echo");
        await using var _ = factory;

        using var response = await browser.PostTokenAsync(new { name = "CI job", permissions = "Read" });
        var created = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var token = created["token"]!.GetValue<string>();
        token.Should().StartWith("spat_");
        response.Headers.CacheControl!.NoStore.Should().BeTrue("the one answer carrying the token must not be cached");

        var overview = (await browser.AgentsAsync()).ToJsonString();
        overview.Should().NotContain(token).And.NotContain(token.Split('.')[2], "the list never shows the token")
            .And.NotContain(PersonalAccessTokens.Hash(token), "nor its hash");

        using var session = Store.OpenAsyncSession();
        var grant = await session.LoadAsync<AgentGrant>(AgentGrantIds.Prefix(userId) + created["key"]!.GetValue<string>());
        grant.Should().NotBeNull();
        grant!.TokenHash.Should().Be(PersonalAccessTokens.Hash(token));
        System.Text.Json.JsonSerializer.Serialize(grant).Should().NotContain(token.Split('.')[2], "only the hash is stored");
        (grant.ExpiresAt!.Value - grant.CreatedAt).Should().Be(TimeSpan.FromDays(90), "the default lifetime (D11)");
    }

    [Fact]
    public async Task A_lifetime_beyond_a_year_is_refused()
    {
        var (factory, browser, _) = await StartAsync("lifetime");
        await using var _f = factory;

        using var tooLong = await browser.PostTokenAsync(new { name = "forever", permissions = "Read", expiresInDays = 366 });
        using var aYear = await browser.PostTokenAsync(new { name = "a year", permissions = "Read", expiresInDays = 365 });

        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        aYear.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_token_calls_mcp_as_its_user_with_the_users_groups_from_the_store()
    {
        var (factory, browser, userId) = await StartAsync("caller");
        await using var _ = factory;
        var (token, _) = await browser.CreateTokenAsync("agent");
        var client = factory.CreateClient();

        var whoami = await CallAsync(client, "acme_whoami", new { }, McpBrowser.Token(token));
        var describe = await DescribeAsync(client, "GuardedDoc", token);

        whoami.ToolText.Should().Contain($"authenticated=True;id={userId};scheme={PersonalAccessTokens.Scheme}");
        describe.IsToolError.Should().BeFalse(describe.ToolText + " — the token carries no groups; Developers must come from the store");
    }

    [Fact]
    public async Task Removing_the_users_role_denies_the_live_tokens_next_call()
    {
        var (factory, browser, userId) = await StartAsync("s4");
        await using var _ = factory;
        var (token, _) = await browser.CreateTokenAsync("agent");
        var client = factory.CreateClient();

        (await DescribeAsync(client, "GuardedDoc", token)).IsToolError.Should().BeFalse();

        // RemoveFromRoleAsync leaves the security stamp alone (spike S4): only the store read can see it.
        await McpAccountHost.ChangeUserAsync(factory, userId, (users, user) => users.RemoveFromRoleAsync(user, McpAccountHost.Developers));
        var after = await DescribeAsync(client, "GuardedDoc", token);

        after.Status.Should().Be(HttpStatusCode.OK, "the token itself is still valid");
        after.ToolText.Should().Be(SparkMcpResult.NotFoundMessage, "the group is gone on the very next call, with no new sign-in");
    }

    // ── the sign-in cookie on /mcp ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cookie <c>/spark/auth/login</c> really issues, on <c>/mcp</c> (D3; §6.2 "cookie on /mcp ignored", "bearer + cookie
    /// runs as the bearer", "authenticated-scheme feature reset"; <c>McpServerTests</c> has the stub-cookie cases). The same
    /// cookie signs the user in on the web; on <c>/mcp</c> it is anonymous alone, and beside another user's personal token
    /// the token decides, with the token's scheme in the feature.
    /// </summary>
    [Fact]
    public async Task The_sign_in_cookie_is_ignored_on_mcp_and_beside_a_token_the_token_decides()
    {
        var (factory, browser, _) = await StartAsync("cookie");
        await using var _f = factory;
        var other = await McpAccountHost.CreateUserAsync(factory, Name("cookie-token"));
        var (token, _) = await (await McpBrowser.SignInAsync(factory.CreateClient(), Name("cookie-token"))).CreateTokenAsync("agent");
        var client = factory.CreateClient();
        Action<HttpRequestMessage> cookie = r => r.Headers.Add("Cookie", browser.CookieHeader);

        using var me = await browser.SendAsync(HttpMethod.Get, "/spark/auth/me");
        var web = await me.Content.ReadAsStringAsync();
        var cookieOnly = await CallAsync(client, "acme_whoami", new { }, cookie);
        var cookieDescribe = await CallAsync(client, "acme_describe", new { type = "GuardedDoc" }, cookie);
        var both = await CallAsync(client, "acme_whoami", new { }, Both(McpBrowser.Token(token), cookie));

        web.Should().Contain("\"isAuthenticated\":true", "the cookie is a live sign-in on the web");
        cookieOnly.ToolText.Should().Be("authenticated=False;id=;scheme=", "/mcp ignores the cookie, and the scheme feature does not name the cookie scheme");
        cookieDescribe.IsToolError.Should().BeTrue("the cookie's user may read GuardedDoc on the web, the anonymous /mcp caller may not");
        both.ToolText.Should().Be($"authenticated=True;id={other.Id};scheme={PersonalAccessTokens.Scheme}", "beside a token the token decides");
    }

    /// <summary>
    /// <c>RequireSignIn</c> on a host with personal tokens only (§6.2 "RequireSignIn 401s an unauthenticated initialize";
    /// the other cases run on stub schemes): no OAuth server is wired, so the challenge is a plain <c>Bearer</c>, and a
    /// real personal token connects.
    /// </summary>
    [Fact]
    public async Task With_RequireSignIn_an_unauthenticated_initialize_is_a_401_and_a_personal_token_connects()
    {
        var (factory, browser, _) = await StartAsync("required", mcp => mcp.RequireSignIn = true);
        await using var _f = factory;
        var (token, _) = await browser.CreateTokenAsync("agent");
        var client = factory.CreateClient();

        var anonymous = await InitializeAsync(client);
        var signedIn = await InitializeAsync(client, McpBrowser.Token(token));

        anonymous.Status.Should().Be(HttpStatusCode.Unauthorized, "every client signs in at connect (D17, S11)");
        anonymous.Response.Headers.WwwAuthenticate.ToString().Should().StartWith("Bearer").And.NotContain("resource_metadata",
            "personal tokens alone publish no protected-resource document");
        signedIn.Status.Should().Be(HttpStatusCode.OK);
    }

    // ── revocation ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_revoked_token_is_a_401()
    {
        var (factory, browser, _) = await StartAsync("revoke");
        await using var _f = factory;
        var (token, key) = await browser.CreateTokenAsync("agent");
        var client = factory.CreateClient();
        (await ListToolsAsync(client, McpBrowser.Token(token))).Status.Should().Be(HttpStatusCode.OK);

        using var revoke = await browser.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/revoke", new { key });
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await ListToolsAsync(client, McpBrowser.Token(token));
        after.Status.Should().Be(HttpStatusCode.Unauthorized, "a revoked token is refused, never run as anonymous");
    }

    [Fact]
    public async Task An_expired_token_is_a_401()
    {
        var (factory, browser, userId) = await StartAsync("expired");
        await using var _f = factory;
        var (token, key) = await browser.CreateTokenAsync("agent");
        var id = AgentGrantIds.Prefix(userId) + key;

        using (var session = Store.OpenAsyncSession())
        {
            var grant = await session.LoadAsync<AgentGrant>(id);
            grant.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await session.SaveChangesAsync();
        }
        factory.GetService<AgentGrantStore>().Invalidate(id);

        (await ListToolsAsync(factory.CreateClient(), McpBrowser.Token(token))).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_security_stamp_change_revokes_every_token_of_the_user()
    {
        var (factory, browser, userId) = await StartAsync("stamp");
        await using var _f = factory;
        var (first, _) = await browser.CreateTokenAsync("one");
        var (second, _) = await browser.CreateTokenAsync("two");
        var client = factory.CreateClient();

        await McpAccountHost.ChangeUserAsync(factory, userId, (users, user) => users.UpdateSecurityStampAsync(user));

        (await ListToolsAsync(client, McpBrowser.Token(first))).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await ListToolsAsync(client, McpBrowser.Token(second))).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await browser.AgentsAsync())["grants"]!.AsArray().Select(g => g!["status"]!.GetValue<string>())
            .Should().Equal(["revoked", "revoked"], "the page says why the tokens stopped working");
    }

    [Fact]
    public async Task A_locked_out_user_s_token_is_a_401()
    {
        var (factory, browser, userId) = await StartAsync("locked");
        await using var _f = factory;
        var (token, _) = await browser.CreateTokenAsync("agent");

        await McpAccountHost.ChangeUserAsync(factory, userId, async (users, user) =>
        {
            await users.SetLockoutEnabledAsync(user, true);
            return await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1));
        });

        (await ListToolsAsync(factory.CreateClient(), McpBrowser.Token(token))).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_tampered_token_is_a_401()
    {
        var (factory, browser, _) = await StartAsync("tamper");
        await using var _f = factory;
        var (token, _) = await browser.CreateTokenAsync("agent");
        var tampered = token[..^2] + (token[^2] == 'A' ? "BB" : "AA");

        (await ListToolsAsync(factory.CreateClient(), McpBrowser.Token(tampered))).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── the grant narrows ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_grant_narrows_tools_and_calls_and_an_edit_applies_to_the_next_call()
    {
        var (factory, browser, _) = await StartAsync("narrow");
        await using var _f = factory;
        var (token, key) = await browser.CreateTokenAsync("agent", "Read", ["GuardedDoc"]);
        var client = factory.CreateClient();

        var tools = await ListToolsAsync(client, McpBrowser.Token(token));
        var discover = await CallAsync(client, "acme_discover", new { }, McpBrowser.Token(token));
        var allowed = await DescribeAsync(client, "GuardedDoc", token);
        var excluded = await DescribeAsync(client, "FilteredDoc", token);

        tools.ToolNames.Should().NotContain("acme_touch", "a read-only grant hides write tools, though the app allows Write");
        discover.ToolText.Should().Contain("\"GuardedDoc\"").And.NotContain("FilteredDoc", "a type outside the grant is not there");
        allowed.IsToolError.Should().BeFalse(allowed.ToolText);
        excluded.ToolText.Should().Be(SparkMcpResult.NotFoundMessage, "the user may read FilteredDoc, but did not grant it to this agent (D13)");

        using var update = await browser.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/update",
            new { key, permissions = "Read, Write", types = (object?)null });
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        (await ListToolsAsync(client, McpBrowser.Token(token))).ToolNames.Should().Contain("acme_touch", "the edit applies without a new sign-in");
        (await DescribeAsync(client, "FilteredDoc", token)).IsToolError.Should().BeFalse("everything exposed now includes FilteredDoc");
    }

    [Fact]
    public async Task More_than_the_app_and_type_allow_cannot_be_granted()
    {
        var (factory, browser, _) = await StartAsync("beyond",
            mcp => mcp.AgentPermissions = agent => agent.Kind == EAgentKind.PersonalAccessToken ? EAgentPermission.Read : EAgentPermission.Read | EAgentPermission.Write);
        await using var _f = factory;

        using var write = await browser.PostTokenAsync(new { name = "w", permissions = "Read, Write" });
        using var unknown = await browser.PostTokenAsync(new { name = "u", permissions = "Read", types = new[] { new { name = "NoSuchType", permissions = "Read" } } });
        var overview = await browser.AgentsAsync();

        write.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await write.Content.ReadAsStringAsync()).Should().Contain("agent_permission_not_offered");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("agent_unknown_type");
        overview["tokenPermissions"]!.GetValue<string>().Should().Be("Read", "the page offers only what the app allows for tokens");
        overview["types"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Should().BeEquivalentTo(["FilteredDoc", "GuardedDoc"]);
    }

    [Fact]
    public async Task Types_the_user_may_not_see_are_not_offered()
    {
        var (factory, browser, _) = await StartAsync("outsider", developer: false);
        await using var _f = factory;

        var overview = await browser.AgentsAsync();

        overview["types"]!.AsArray().Count.Should().Be(0, "only Developers may see the exposed types; offering them would reveal they exist");
    }

    // ── ownership and the web ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_user_cannot_list_change_or_revoke_another_users_grant()
    {
        var (factory, alice, aliceId) = await StartAsync("alice");
        await using var _f = factory;
        var (token, key) = await alice.CreateTokenAsync("agent", "Read", ["GuardedDoc"]);
        await McpAccountHost.CreateUserAsync(factory, Name("bob"));
        var bob = await McpBrowser.SignInAsync(factory.CreateClient(), Name("bob"));

        var bobsList = await bob.AgentsAsync();
        using var update = await bob.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/update", new { key, permissions = "Read, Write", types = (object?)null });
        using var revoke = await bob.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/revoke", new { key });
        using var fullId = await bob.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/revoke", new { key = AgentGrantIds.Prefix(aliceId) + key });
        using var traversal = await bob.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/revoke", new { key = "../" + aliceId + "/" + key });

        bobsList["grants"]!.AsArray().Count.Should().Be(0);
        update.StatusCode.Should().Be(HttpStatusCode.NotFound, "the key is resolved under bob's own prefix");
        revoke.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fullId.StatusCode.Should().Be(HttpStatusCode.NotFound, "a full id is not a key");
        traversal.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var still = await CallAsync(factory.CreateClient(), "acme_discover", new { }, McpBrowser.Token(token));
        still.Status.Should().Be(HttpStatusCode.OK);
        still.ToolText.Should().NotContain("FilteredDoc", "alice's grant is unchanged");
    }

    [Fact]
    public async Task A_token_opens_no_web_endpoint()
    {
        var (factory, browser, _) = await StartAsync("web");
        await using var _f = factory;
        var (token, _) = await browser.CreateTokenAsync("agent", "Read, Write");
        var client = factory.CreateClient();
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new GuardedDoc { Name = "web", IsVisible = true }, "GuardedDocs/pat-web-" + Scope);
            await session.SaveChangesAsync();
        }

        async Task<HttpStatusCode> SendAsync(HttpMethod method, string url, object? body = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            McpBrowser.Token(token)(request);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        using var me = await browser.SendAsync(HttpMethod.Get, "/spark/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture's cookie works");

        (await SendAsync(HttpMethod.Get, "/spark/auth/manage/agents")).Should().NotBe(HttpStatusCode.OK,
            "an agent must not read or widen its own grant");
        (await SendAsync(HttpMethod.Post, "/spark/po/load", new { objectTypeId = "GuardedDoc", id = "GuardedDocs/pat-web-" + Scope }))
            .Should().Be(HttpStatusCode.Unauthorized, "on the web the token is no credential, so the caller is anonymous (D3, D13: the agent layers exist only on /mcp)");
        var meWithToken = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/spark/auth/me").With(McpBrowser.Token(token)));
        (await meWithToken.Content.ReadAsStringAsync()).Should().NotContain(Name("web"));
    }

    // ── audit ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_call_records_last_use_and_the_overview_shows_it()
    {
        var (factory, browser, _) = await StartAsync("lastused");
        await using var _f = factory;
        var (token, key) = await browser.CreateTokenAsync("agent");

        (await ListToolsAsync(factory.CreateClient(), McpBrowser.Token(token))).Status.Should().Be(HttpStatusCode.OK);

        var grant = (await browser.AgentsAsync())["grants"]!.AsArray().Single(g => g!["key"]!.GetValue<string>() == key)!;
        grant["lastUsedAt"].Should().NotBeNull("D15: LastUsedAt is written on use (at most once a minute)");
        grant["kind"]!.GetValue<string>().Should().Be("PersonalAccessToken");
        grant["status"]!.GetValue<string>().Should().Be("active");
    }

    [Fact]
    public async Task An_explicit_personal_access_token_flag_starts_when_the_Authorization_package_is_wired()
    {
        // The factory starts the host, which resolves the setups: an explicit flag without its wiring throws there.
        await using var factory = McpAccountHost.Create(Store, mcp => mcp.Authentication = EAgentAuthentication.PersonalAccessToken);

        (await ListToolsAsync(factory.CreateClient())).Status.Should().Be(HttpStatusCode.OK);
    }
}

file static class RequestExtensions
{
    public static HttpRequestMessage With(this HttpRequestMessage request, Action<HttpRequestMessage> configure)
    {
        configure(request);
        return request;
    }
}
