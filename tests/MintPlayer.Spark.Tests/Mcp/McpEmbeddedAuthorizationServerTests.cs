using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Mcp;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// Setup 3 end to end (MCP PRD D4, D5, D6, D12, D17, §6.2; plan M3b): the embedded authorization server with
/// <c>idp.ForMcp()</c> signs an agent in through a Client ID Metadata Document or anonymous loopback registration,
/// code + PKCE with <c>resource</c>, the agent consent screen, and <c>/mcp</c> accepting exactly its tokens. Real
/// Identity, real RavenDB, the real IdentityProvider and Mcp gate; a scripted public client (the spike's approach).
/// </summary>
public class McpEmbeddedAuthorizationServerTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string LoopbackRedirect = "http://127.0.0.1:53682/callback";

    private string Name(string localPart) => $"{localPart}-{Scope}";

    private string DocumentUrl(string name) => $"https://agent.example/{Scope}/{name}.json";

    /// <summary>A host, a user, and a metadata-document client served by the stub document server.</summary>
    private async Task<(SparkEndpointFactory<GuardedContext> Factory, McpAgentClient Agent, SparkUser User)> StartAsync(
        string user, Action<MintPlayer.Spark.Mcp.SparkMcpOptions>? configureMcp = null)
    {
        var clientId = DocumentUrl("claude");
        var documents = new McpEmbeddedHost.DocumentServer().Serve(clientId, McpEmbeddedHost.Document(clientId));
        var factory = McpEmbeddedHost.Create(Store, documents, configureMcp);
        var created = await McpAccountHost.CreateUserAsync(factory, Name(user));
        return (factory, new McpAgentClient(factory.CreateClient(), clientId, LoopbackRedirect), created);
    }

    private static JsonNode Payload(string jwt) => JsonNode.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1]))!;

    private static JsonNode Header(string jwt) => JsonNode.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[0]))!;

    private static Task<Reply> WhoAmIAsync(HttpClient client, string token)
        => CallAsync(client, "acme_whoami", new { }, McpAgentClient.Bearer(token));

    // ── metadata ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_metadata_documents_advertise_what_MCP_clients_look_for()
    {
        await using var factory = McpEmbeddedHost.Create(Store);
        var client = factory.CreateClient();

        var server = (await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server"))!;
        server["issuer"]!.GetValue<string>().Should().Be(McpEmbeddedHost.Issuer);
        server["client_id_metadata_document_supported"]!.GetValue<bool>().Should().BeTrue("Codex uses CIMD only when advertised (S3)");
        server["authorization_response_iss_parameter_supported"]!.GetValue<bool>().Should().BeTrue();
        Strings(server["token_endpoint_auth_methods_supported"]).Should().Contain("none");
        Strings(server["code_challenge_methods_supported"]).Should().Equal("S256");
        Strings(server["scopes_supported"]).Should().Equal("mcp:read", "mcp:write", "offline_access");
        server["registration_endpoint"]!.GetValue<string>().Should().Be(McpEmbeddedHost.Issuer + "/connect/register");

        var resource = (await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-protected-resource/mcp"))!;
        resource["resource"]!.GetValue<string>().Should().Be(McpTestHost.CanonicalUrl);
        Strings(resource["authorization_servers"]).Should().Equal(McpEmbeddedHost.Issuer);

        var anonymous = await InitializeAsync(client);
        anonymous.Status.Should().Be(HttpStatusCode.OK, "RequireSignIn is off: anonymous use keeps working");
    }

    // ── the flows ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_metadata_document_client_signs_in_with_code_and_PKCE_and_calls_mcp()
    {
        var (factory, agent, user) = await StartAsync("cimd");
        await using var _ = factory;
        await agent.SignInAsync(user.Email!);

        var (html, requestId, page) = await agent.ConsentPageAsync();
        page.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        page.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'");
        html.Should().Contain("data-agent-trust=\"verified\"").And.Contain("agent.example", "the verified host is shown (D7)");
        html.Should().Contain("data-agent-loopback", "a loopback client is said to run on the user's computer");
        html.Should().Contain("value=\"GuardedDoc\"", "the exposed types the user may see are offered");

        var consent = await agent.PostConsentAsync(html, requestId);
        consent.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var code = System.Web.HttpUtility.ParseQueryString(consent.Headers.Location!.Query)["code"]!;
        var (status, tokens) = await agent.RedeemAsync(code);
        status.Should().Be(HttpStatusCode.OK, tokens.ToJsonString());

        var access = tokens["access_token"]!.GetValue<string>();
        Header(access)["typ"]!.GetValue<string>().Should().Be("at+jwt");
        var payload = Payload(access);
        Strings(payload["aud"] is JsonArray ? payload["aud"] : new JsonArray(payload["aud"]!.GetValue<string>()))
            .Should().Equal([McpTestHost.CanonicalUrl], "the audience is the canonical URL, nothing else (D5 G4)");
        payload["sub"]!.GetValue<string>().Should().Be(user.Id, "public sub = the local user id (G10)");
        payload["iss"]!.GetValue<string>().Should().Be(McpEmbeddedHost.Issuer);
        tokens["refresh_token"].Should().NotBeNull("agent clients get refresh tokens (G8)");

        var whoami = await WhoAmIAsync(factory.CreateClient(), access);
        whoami.Status.Should().Be(HttpStatusCode.OK);
        whoami.ToolText.Should().Contain($"id={user.Id}").And.Contain("authenticated=True");

        using var session = Store.OpenAsyncSession();
        var grant = await session.LoadAsync<AgentGrant>(AgentGrantIds.Application(user.Id!, agent.ClientId));
        grant.Should().NotBeNull("the consent writes the agent grant (D12)");
        grant!.Kind.Should().Be(EAgentKind.MetadataDocument);
        grant.VerifiedHost.Should().Be("agent.example");
        grant.Permissions.Should().Be(EAgentPermission.Read);
        grant.OidcGrantId.Should().Be(OidcGrantReference.DocumentId(user.Id!, OidcAgentClients.ApplicationId(agent.ClientId)));
    }

    [Fact]
    public async Task A_dynamically_registered_client_signs_in_on_any_loopback_port_and_path()
    {
        await using var factory = McpEmbeddedHost.Create(Store);
        var user = await McpAccountHost.CreateUserAsync(factory, Name("dcr"));
        var client = factory.CreateClient();

        var registered = await client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Gemini CLI",
            redirect_uris = new[] { "http://localhost:7777/oauth/callback" },
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
        });
        registered.StatusCode.Should().Be(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var body = (await registered.Content.ReadFromJsonAsync<JsonObject>())!;
        body.ContainsKey("registration_access_token").Should().BeFalse("an anonymous registration has nothing to manage");
        var clientId = body["client_id"]!.GetValue<string>();

        // Another port, another path, the other loopback name (RFC 8252 §7.3, S3).
        var agent = new McpAgentClient(client, clientId, "http://127.0.0.1:53682/other/path");
        await agent.SignInAsync(user.Email!);
        var (html, requestId, _) = await agent.ConsentPageAsync();
        html.Should().Contain("data-agent-trust=\"unverified\"", "a registered name proves nothing (D5, D7)");
        var consent = await agent.PostConsentAsync(html, requestId);
        var code = System.Web.HttpUtility.ParseQueryString(consent.Headers.Location!.Query)["code"]!;
        var (status, tokens) = await agent.RedeemAsync(code);
        status.Should().Be(HttpStatusCode.OK, tokens.ToJsonString());

        (await WhoAmIAsync(client, tokens["access_token"]!.GetValue<string>())).ToolText.Should().Contain($"id={user.Id}");
        (await agent.AuthorizeAsync(redirect: "https://evil.example/callback")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "only loopback redirects match loosely, never another host");
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("http://evil.example/callback")]
    [InlineData("http://localhost.evil.example/callback")]
    [InlineData("myagent://callback")]
    [InlineData("http://user@127.0.0.1/callback")]
    public async Task Anonymous_registration_refuses_a_non_loopback_redirect(string redirect)
    {
        await using var factory = McpEmbeddedHost.Create(Store);

        var response = await factory.CreateClient().PostAsJsonAsync("/connect/register", new { client_name = "x", redirect_uris = new[] { "http://127.0.0.1/cb", redirect } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("invalid_redirect_uri");
    }

    [Fact]
    public async Task Anonymous_registration_accepts_exactly_VS_Codes_redirects()
    {
        await using var factory = McpEmbeddedHost.Create(Store);

        // What VS Code sends (microsoft/vscode src/vs/base/common/oauth.ts:947-972).
        var response = await factory.CreateClient().PostAsJsonAsync("/connect/register", new
        {
            client_name = "Visual Studio Code",
            redirect_uris = new[] { "https://insiders.vscode.dev/redirect", "https://vscode.dev/redirect", "http://127.0.0.1/", "http://127.0.0.1:33418/" },
            token_endpoint_auth_method = "none",
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var confidential = await factory.CreateClient().PostAsJsonAsync("/connect/register", new
        {
            redirect_uris = new[] { "http://127.0.0.1/cb" }, token_endpoint_auth_method = "client_secret_basic",
        });
        confidential.StatusCode.Should().Be(HttpStatusCode.BadRequest, "anonymous registration makes public clients only");
    }

    // ── audience ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_resource_other_than_the_MCP_endpoint_is_invalid_target()
    {
        var (factory, agent, user) = await StartAsync("target");
        await using var _ = factory;
        await agent.SignInAsync(user.Email!);

        var other = await agent.AuthorizeAsync(resource: "https://acme.example/other-api");
        other.StatusCode.Should().Be(HttpStatusCode.Redirect);
        System.Web.HttpUtility.ParseQueryString(other.Headers.Location!.Query)["error"].Should().Be("invalid_target",
            "an unknown resource never silently becomes aud = client_id (D5 G4)");

        var code = await agent.CodeAsync();
        var (status, body) = await agent.RedeemAsync(code, resource: "https://acme.example/other-api");
        status.Should().Be(HttpStatusCode.BadRequest);
        body["error"]!.GetValue<string>().Should().Be("invalid_target");

        // Without a resource the canonical URL is implied.
        var implied = await agent.AuthorizeAsync(resource: null);
        implied.Headers.Location!.OriginalString.Should().StartWith("/connect/consent");
    }

    [Fact]
    public async Task A_token_for_another_resource_is_refused_on_mcp()
    {
        var (factory, agent, user) = await StartAsync("aud");
        await using var _ = factory;
        var tokens = await agent.SignInAndRedeemAsync(user.Email!);
        var good = tokens["access_token"]!.GetValue<string>();
        var authorizationId = OidcGrantReference.DocumentId(user.Id!, OidcAgentClients.ApplicationId(agent.ClientId));

        // The same user, client, grant and a live record: only the audience differs.
        var generator = factory.GetService<OidcTokenGenerator>();
        using (var session = Store.OpenAsyncSession())
        {
            var app = await session.LoadAsync<OidcApplication>(OidcAgentClients.ApplicationId(agent.ClientId));
            var fleet = new OidcScopeDefinition("fleet.read", "fleet", OidcResourceKinds.Api, null, null, [], false, false, false);
            var (token, jti) = generator.GenerateAccessToken(user, app!, McpEmbeddedHost.Issuer, [fleet], 5);
            await session.StoreAsync(new OidcToken
            {
                Id = OidcTokenReference.DocumentId(jti), ApplicationId = app!.Id!, AuthorizationId = authorizationId, Subject = user.Id!,
                Type = OidcTokenTypes.AccessToken, Scopes = ["fleet.read"], Status = "valid", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            });
            await session.SaveChangesAsync();

            var client = factory.CreateClient();
            (await WhoAmIAsync(client, good)).Status.Should().Be(HttpStatusCode.OK);
            var refused = await WhoAmIAsync(client, token);
            refused.Status.Should().Be(HttpStatusCode.Unauthorized, "a token for another resource never authenticates /mcp, never as anonymous either");
            refused.Response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
        }

        // An ID token is not an access token.
        var idTokenCaller = await WhoAmIAsync(factory.CreateClient(), tokens["access_token"]!.GetValue<string>().Split('.')[0] + ".e30.x");
        idTokenCaller.Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── refresh ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reusing_a_refresh_token_revokes_the_whole_family()
    {
        var (factory, agent, user) = await StartAsync("reuse");
        await using var _ = factory;
        var first = (await agent.SignInAndRedeemAsync(user.Email!))["refresh_token"]!.GetValue<string>();

        var (rotated, second) = await agent.RefreshAsync(first, McpTestHost.CanonicalUrl);
        rotated.Should().Be(HttpStatusCode.OK, second.ToJsonString());
        var secondRefresh = second["refresh_token"]!.GetValue<string>();
        secondRefresh.Should().NotBe(first, "every refresh rotates");
        var secondAccess = second["access_token"]!.GetValue<string>();
        (await WhoAmIAsync(factory.CreateClient(), secondAccess)).Status.Should().Be(HttpStatusCode.OK);

        // The teardown finds the successor through an index, best-effort by design (Token.RevokeAuthorizationChainAsync),
        // so a token issued moments before the replay may be missed: on the class's shared database the index carries
        // every earlier case's tokens, and the race showed (the successor refreshed, 200, in a run beside the
        // IdentityProvider namespace, 2026-10-12). The wait pins what this case is about, as OidcTokenSecurityTests' do:
        // a chain the index can see is torn down.
        await Store.WaitForIndexingAsync();
        (await agent.RefreshAsync(first)).Status.Should().Be(HttpStatusCode.BadRequest, "a redeemed refresh token presented again is a theft signal");
        (await agent.RefreshAsync(secondRefresh)).Status.Should().Be(HttpStatusCode.BadRequest, "the reuse revoked the whole family");
        (await WhoAmIAsync(factory.CreateClient(), secondAccess)).Status.Should().Be(HttpStatusCode.Unauthorized, "and its access tokens");
    }

    // ── revocation in both directions (D12) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Withdrawing_the_OAuth_grant_revokes_the_agent_grant()
    {
        var (factory, agent, user) = await StartAsync("withdraw");
        await using var _ = factory;
        var access = (await agent.SignInAndRedeemAsync(user.Email!))["access_token"]!.GetValue<string>();
        var appId = OidcAgentClients.ApplicationId(agent.ClientId);

        (await factory.GetService<OidcGrantWithdrawal>().WithdrawAsync(user.Id!, appId, null, null, CancellationToken.None)).Should().BeTrue();

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<AgentGrant>(AgentGrantIds.Application(user.Id!, agent.ClientId))).Should().BeNull("revoking either revokes both");
        (await WhoAmIAsync(factory.CreateClient(), access)).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_the_agent_grant_revokes_the_OAuth_grant_and_its_refresh_token()
    {
        var (factory, agent, user) = await StartAsync("revoke");
        await using var _ = factory;
        var tokens = await agent.SignInAndRedeemAsync(user.Email!);

        var browser = await McpBrowser.SignInAsync(factory.CreateClient(), user.UserName!);
        using var revoke = await browser.SendAsync(HttpMethod.Post, "/spark/auth/manage/agents/revoke", new { key = "app/" + agent.ClientId });
        revoke.IsSuccessStatusCode.Should().BeTrue(await revoke.Content.ReadAsStringAsync());

        (await WhoAmIAsync(factory.CreateClient(), tokens["access_token"]!.GetValue<string>())).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await agent.RefreshAsync(tokens["refresh_token"]!.GetValue<string>())).Status.Should().Be(HttpStatusCode.BadRequest,
            "the linked OAuth grant was withdrawn, so the agent cannot mint a new token either");
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<OidcGrant>(OidcGrantReference.DocumentId(user.Id!, OidcAgentClients.ApplicationId(agent.ClientId))))!
            .Status.Should().Be("revoked");
    }

    // ── consent ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Consent_offers_only_what_AgentPermissions_allows_this_agent()
    {
        var (factory, agent, user) = await StartAsync("limited",
            mcp => mcp.AgentPermissions = a => a.Kind == EAgentKind.MetadataDocument ? EAgentPermission.Read : EAgentPermission.Read | EAgentPermission.Write);
        await using var _ = factory;
        await agent.SignInAsync(user.Email!);

        var (html, requestId, _) = await agent.ConsentPageAsync();
        html.Should().NotContain("value=\"write\"", "changes are not offered when the app layer withholds Write for this agent");

        var forged = await agent.PostConsentAsync(html, requestId, permission: "write");
        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a forged POST cannot widen the choice");
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<AgentGrant>(AgentGrantIds.Application(user.Id!, agent.ClientId))).Should().BeNull();
    }

    [Fact]
    public async Task Changes_allowed_on_the_consent_screen_reach_the_grant_and_the_scope()
    {
        var (factory, agent, user) = await StartAsync("write");
        await using var _ = factory;
        var tokens = await agent.SignInAndRedeemAsync(user.Email!, permission: "write");

        Payload(tokens["access_token"]!.GetValue<string>())["scope"]!.GetValue<string>().Split(' ').Should().Contain("mcp:write");
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<AgentGrant>(AgentGrantIds.Application(user.Id!, agent.ClientId)))!.Permissions
            .Should().Be(EAgentPermission.Read | EAgentPermission.Write);
    }

    [Fact]
    public async Task A_locked_account_cannot_consent()
    {
        var (factory, agent, user) = await StartAsync("locked");
        await using var _ = factory;
        await agent.SignInAsync(user.Email!);
        var (html, requestId, _) = await agent.ConsentPageAsync();

        await McpAccountHost.ChangeUserAsync(factory, user.Id!, (users, u) => users.SetLockoutEndDateAsync(u, DateTimeOffset.UtcNow.AddDays(1)));

        (await agent.PostConsentAsync(html, requestId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await agent.ConsentPageAsync()).Page.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── housekeeping ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unused_dynamic_registration_is_pruned_with_its_grants()
    {
        await using var factory = McpEmbeddedHost.Create(Store);
        var user = await McpAccountHost.CreateUserAsync(factory, Name("prune"));
        var client = factory.CreateClient();
        var registered = await client.PostAsJsonAsync("/connect/register", new { client_name = "old", redirect_uris = new[] { "http://127.0.0.1/cb" } });
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();
        var agent = new McpAgentClient(client, clientId, "http://127.0.0.1:4000/cb");
        var access = (await agent.SignInAndRedeemAsync(user.Email!))["access_token"]!.GetValue<string>();

        var cleanup = factory.GetService<IEnumerable<IHostedService>>().OfType<OidcAgentClientCleanupService>().Single();
        (await cleanup.PruneAsync(DateTime.UtcNow, CancellationToken.None)).Should().Be(0, "a client used just now stays");

        await Store.WaitForIndexingAsync();
        var pruned = await cleanup.PruneAsync(DateTime.UtcNow.AddDays(31), CancellationToken.None);
        pruned.Should().BeGreaterThan(0);

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<OidcApplication>(OidcAgentClients.ApplicationId(clientId))).Should().BeNull();
        (await session.LoadAsync<AgentGrant>(AgentGrantIds.Application(user.Id!, clientId))).Should().BeNull();
        (await WhoAmIAsync(client, access)).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static IEnumerable<string> Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>());
}
