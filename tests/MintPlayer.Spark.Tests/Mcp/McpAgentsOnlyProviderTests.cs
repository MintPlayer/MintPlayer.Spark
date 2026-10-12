using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>
/// The provider an application hosts only to sign AI agents in to its own <c>/mcp</c>: <c>idp.ForMcp().AgentsOnly()</c>
/// (MCP PRD decision log, "IdP surface for MCP-only apps"). It maps what agents use (the metadata documents, JWKS, authorize
/// with sign-in and consent, the token endpoint with code and refresh, anonymous registration, revocation), answers 404 on
/// everything else, advertises only what it maps, and the M3b agent flows (metadata document, loopback registration,
/// refresh) still work through it.
/// </summary>
public class McpAgentsOnlyProviderTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private const string LoopbackRedirect = "http://127.0.0.1:53682/callback";

    /// <summary>What a full provider maps beside the agents' surface; an agents-only provider maps none of it.</summary>
    internal static readonly (string Method, string Path)[] NotForAgents =
    [
        ("POST", "/connect/device_authorization"),
        ("GET", "/connect/device"),
        ("POST", "/connect/device"),
        ("POST", "/connect/par"),
        ("POST", "/connect/introspect"),
        ("GET", "/connect/userinfo"),
        ("POST", "/connect/userinfo"),
        ("GET", "/connect/logout"),
        ("GET", "/connect/applications"),
        ("POST", "/connect/applications/revoke"),
        ("POST", "/connect/applications/revoke-scope"),
        ("GET", "/connect/register/some-client"),
        ("PUT", "/connect/register/some-client"),
        ("DELETE", "/connect/register/some-client"),
        ("GET", "/spark/identity-provider/developer"),
        ("POST", "/spark/identity-provider/developer"),
        ("POST", "/spark/identity-provider/developer/registration-token"),
        ("POST", "/spark/identity-provider/invitations/accept"),
        ("GET", "/spark/identity-provider/applications"),
        ("POST", "/spark/identity-provider/applications/withdraw"),
        ("GET", "/spark/identity-provider/admin/keys"),
        ("POST", "/spark/identity-provider/admin/keys/rotate"),
    ];

    /// <summary>Keys of the discovery documents that name something an agents-only provider does not offer.</summary>
    internal static readonly string[] NotAdvertisedForAgents =
    [
        "userinfo_endpoint", "end_session_endpoint", "introspection_endpoint", "pushed_authorization_request_endpoint",
        "device_authorization_endpoint", "introspection_endpoint_auth_methods_supported", "userinfo_signing_alg_values_supported",
        "userinfo_encryption_alg_values_supported", "userinfo_encryption_enc_values_supported", "backchannel_logout_supported",
        "backchannel_logout_session_supported", "frontchannel_logout_supported", "frontchannel_logout_session_supported",
    ];

    private string Name(string localPart) => $"{localPart}-{Scope}";

    private string DocumentUrl(string name) => $"https://agent.example/{Scope}/{name}.json";

    private SparkEndpointFactory<GuardedContext> AgentsOnly(McpEmbeddedHost.DocumentServer? documents = null)
        => McpEmbeddedHost.Create(Store, documents, configureProvider: idp => idp.AgentsOnly());

    private static IEnumerable<string> Strings(JsonNode? array) => array!.AsArray().Select(n => n!.GetValue<string>());

    private static Task<Reply> WhoAmIAsync(HttpClient client, string token)
        => CallAsync(client, "acme_whoami", new { }, McpAgentClient.Bearer(token));

    /// <summary>Every route in <paramref name="routes"/> that does not answer 404, with what it answered.</summary>
    internal static async Task<List<string>> MappedAsync(HttpClient client, IEnumerable<(string Method, string Path)> routes)
    {
        var mapped = new List<string>();
        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT")
                request.Content = new FormUrlEncodedContent([]);
            using var response = await client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.NotFound)
                mapped.Add($"{method} {path} answered {(int)response.StatusCode}");
        }
        return mapped;
    }

    /// <summary>
    /// Every endpoint <paramref name="document"/> names is mapped: a GET answers anything but 404 (405 for a POST-only
    /// endpoint), so a document can advertise only what exists.
    /// </summary>
    internal static async Task<List<string>> UnmappedAdvertisedAsync(HttpClient client, JsonObject document, string issuer)
    {
        var unmapped = new List<string>();
        foreach (var (key, value) in document)
        {
            if (!(key.EndsWith("_endpoint", StringComparison.Ordinal) || key == "jwks_uri"))
                continue;
            var path = value!.GetValue<string>()[issuer.Length..];
            using var response = await client.GetAsync(path);
            if (response.StatusCode == HttpStatusCode.NotFound)
                unmapped.Add($"{key} ({path})");
        }
        return unmapped;
    }

    // ── the surface ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Everything_beyond_what_agents_need_answers_404()
    {
        await using var factory = AgentsOnly();
        var client = factory.CreateClient();

        (await MappedAsync(client, NotForAgents)).Should().BeEmpty("an agents-only provider maps none of these");

        // What agents do use is still there.
        (await client.GetAsync("/.well-known/jwks")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/connect/login?returnUrl=%2F")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/connect/token")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "POST-only, but mapped");
        (await client.GetAsync("/connect/revoke")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "POST-only, but mapped");
        (await client.GetAsync("/connect/register")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, "anonymous agent registration stays");
    }

    [Fact]
    public async Task Its_metadata_documents_advertise_only_what_it_maps()
    {
        await using var factory = AgentsOnly();
        var client = factory.CreateClient();

        var server = (await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server"))!;
        Strings(server["grant_types_supported"]).Should().Equal("authorization_code", "refresh_token");
        Strings(server["scopes_supported"]).Should().Equal("mcp:read", "mcp:write", "offline_access");
        server["revocation_endpoint"]!.GetValue<string>().Should().Be(McpEmbeddedHost.Issuer + "/connect/revoke");
        server["registration_endpoint"]!.GetValue<string>().Should().Be(McpEmbeddedHost.Issuer + "/connect/register");
        (await UnmappedAdvertisedAsync(client, server, McpEmbeddedHost.Issuer)).Should().BeEmpty();

        var discovery = (await client.GetFromJsonAsync<JsonObject>("/.well-known/openid-configuration"))!;
        Strings(discovery["grant_types_supported"]).Should().Equal("authorization_code", "refresh_token");
        var keys = discovery.Select(p => p.Key).ToList();
        foreach (var key in new[] { "authorization_endpoint", "token_endpoint", "revocation_endpoint", "registration_endpoint", "jwks_uri" })
            keys.Should().Contain(key);
        keys.Should().NotIntersectWith(NotAdvertisedForAgents);
        (await UnmappedAdvertisedAsync(client, discovery, McpEmbeddedHost.Issuer)).Should().BeEmpty();
    }

    [Fact]
    public async Task Its_token_endpoint_refuses_the_grants_it_does_not_offer()
    {
        await using var factory = AgentsOnly();
        var client = factory.CreateClient();

        foreach (var grant in new[] { "client_credentials", OidcDeviceCodes.GrantType, "urn:ietf:params:oauth:grant-type:token-exchange" })
        {
            var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = grant, ["client_id"] = "any-client", ["client_secret"] = "any-secret",
                ["device_code"] = "any-code", ["subject_token"] = "any-token",
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            }));
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            JsonNode.Parse(body)!["error"]!.GetValue<string>().Should().Be("unsupported_grant_type", $"{grant} is not one of this provider's flows");
        }
    }

    // ── the agent flows ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_metadata_document_agent_signs_in_refreshes_and_revokes()
    {
        var clientId = DocumentUrl("claude");
        var documents = new McpEmbeddedHost.DocumentServer().Serve(clientId, McpEmbeddedHost.Document(clientId));
        await using var factory = AgentsOnly(documents);
        var user = await McpAccountHost.CreateUserAsync(factory, Name("cimd"));
        var client = factory.CreateClient();
        var agent = new McpAgentClient(client, clientId, LoopbackRedirect);

        await agent.SignInAsync(user.Email!);
        var (html, requestId, _) = await agent.ConsentPageAsync();
        html.Should().Contain("data-agent-trust=\"verified\"");
        html.Should().NotContain("/connect/logout", "no end-session endpoint here: the link would be a dead end");
        html.Should().NotContain("/connect/applications", "agents are managed on the account page, not at the provider");
        var consent = await agent.PostConsentAsync(html, requestId);
        var code = System.Web.HttpUtility.ParseQueryString(consent.Headers.Location!.Query)["code"]!;
        var (status, tokens) = await agent.RedeemAsync(code);
        status.Should().Be(HttpStatusCode.OK, tokens.ToJsonString());
        (await WhoAmIAsync(client, tokens["access_token"]!.GetValue<string>())).ToolText.Should().Contain($"id={user.Id}");

        var (refreshed, second) = await agent.RefreshAsync(tokens["refresh_token"]!.GetValue<string>(), McpTestHost.CanonicalUrl);
        refreshed.Should().Be(HttpStatusCode.OK, second.ToJsonString());
        (await WhoAmIAsync(client, second["access_token"]!.GetValue<string>())).Status.Should().Be(HttpStatusCode.OK, "the refreshed token works on /mcp");

        var refreshToken = second["refresh_token"]!.GetValue<string>();
        var revoke = await client.PostAsync("/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = refreshToken, ["token_type_hint"] = "refresh_token", ["client_id"] = clientId,
        }));
        revoke.StatusCode.Should().Be(HttpStatusCode.OK, "an agent may revoke its own tokens (RFC 7009)");
        (await agent.RefreshAsync(refreshToken)).Status.Should().Be(HttpStatusCode.BadRequest, "a revoked refresh token is spent");
    }

    [Fact]
    public async Task An_agent_registers_anonymously_and_a_developers_initial_access_token_registers_nothing()
    {
        await using var factory = AgentsOnly();
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
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();
        var agent = new McpAgentClient(client, clientId, "http://localhost:7777/oauth/callback");
        var tokens = await agent.SignInAndRedeemAsync(user.Email!);
        (await WhoAmIAsync(client, tokens["access_token"]!.GetValue<string>())).ToolText.Should().Contain($"id={user.Id}");

        // A valid initial access token of an approved developer, as the portal would have issued it: developer registration
        // is not part of this provider, so it registers nothing.
        string initialAccessToken;
        using (var session = Store.OpenAsyncSession())
        {
            OidcDevelopers.Set(session, user.Id!, new OidcDeveloper
            {
                Status = OidcDeveloperStatuses.Approved, TermsVersion = 1, RequestedAt = DateTime.UtcNow, DecidedAt = DateTime.UtcNow,
            });
            initialAccessToken = await OidcClientRegistration.IssueInitialAccessTokenAsync(session, user.Id!, CancellationToken.None);
            await session.SaveChangesAsync();
        }
        using var developer = new HttpRequestMessage(HttpMethod.Post, "/connect/register")
        {
            Content = JsonContent.Create(new
            {
                client_name = Name("web-app"),
                redirect_uris = new[] { $"https://{Name("web-app")}.example/cb" },
                grant_types = new[] { "authorization_code" },
                token_endpoint_auth_method = "client_secret_basic",
            }),
        };
        developer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", initialAccessToken);
        var refused = await client.SendAsync(developer);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized, await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_the_refresh_flow_an_agent_gets_no_refresh_token_and_none_is_advertised()
    {
        var clientId = DocumentUrl("no-refresh");
        var documents = new McpEmbeddedHost.DocumentServer().Serve(clientId, McpEmbeddedHost.Document(clientId));
        await using var factory = McpEmbeddedHost.Create(Store, documents, configureProvider: idp =>
        {
            idp.AgentsOnly();
            idp.Flows = SparkIdentityProviderFlows.AuthorizationCode;
        });
        var user = await McpAccountHost.CreateUserAsync(factory, Name("no-refresh"));
        var client = factory.CreateClient();

        var server = (await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server"))!;
        Strings(server["grant_types_supported"]).Should().Equal("authorization_code");
        Strings(server["scopes_supported"]).Should().Equal("mcp:read", "mcp:write");

        var tokens = await new McpAgentClient(client, clientId, LoopbackRedirect).SignInAndRedeemAsync(user.Email!);
        tokens["refresh_token"].Should().BeNull("this provider does not offer refresh tokens, so it issues none");
        (await WhoAmIAsync(client, tokens["access_token"]!.GetValue<string>())).Status.Should().Be(HttpStatusCode.OK);
    }

    // ── what refuses to start ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_agent_server_without_the_authorization_code_flow_refuses_startup()
    {
        var act = () => McpEmbeddedHost.Create(Store, configureProvider: idp => idp.Flows = SparkIdentityProviderFlows.ClientCredentials).CreateClient();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("needs Flows.AuthorizationCode");
    }

    [Fact]
    public void Agents_only_beside_another_applications_endpoint_refuses_startup()
    {
        var act = () => McpRemoteHost.Provider(Store, configureIdp: idp => idp.AgentsOnly()).CreateClient();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("needs Endpoints.UserInfo");
    }
}
