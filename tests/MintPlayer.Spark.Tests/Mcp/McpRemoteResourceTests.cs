using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.IdentityProvider.Mcp;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.IdentityProvider;
using MintPlayer.Spark.Tests.Mcp._Infrastructure;
using static MintPlayer.Spark.Tests.Mcp._Infrastructure.McpWire;

namespace MintPlayer.Spark.Tests.Mcp;

/// <summary>A database of its own for <see cref="McpRemoteHost.OtherEmbeddedServer"/>: another application, another store.</summary>
public sealed class McpRemoteSecondDatabase : SparkSharedDatabase;

/// <summary>
/// A Spark identity provider as the external authorization server (setup 2) of <b>another</b> application's MCP endpoint
/// (remote resources, owner 2026-10-12; demo: SparkId for HR): <c>idp.ForMcpResource(url)</c> signs agents in with the
/// same flows as <c>ForMcp()</c> (metadata documents, loopback registration, PKCE, refresh rotation) and mints tokens
/// whose only audience is that URL; the consent offers read or read + changes, never types; the resource application maps
/// the provider's user through the linked login; no other endpoint accepts the token. Real Identity, IdentityProvider and
/// Mcp gate in each host; a scripted agent.
/// </summary>
public class McpRemoteResourceTests(SparkSharedDatabase database, McpRemoteSecondDatabase second)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>, IClassFixture<McpRemoteSecondDatabase>
{
    private const string LoopbackRedirect = "http://127.0.0.1:53682/callback";

    private string Name(string localPart) => $"{localPart}-{Scope}";

    private string DocumentUrl(string name) => $"https://agent.example/{Scope}/{name}.json";

    /// <summary>The provider, a user of it, and a metadata-document agent asking for <paramref name="resource"/>.</summary>
    private async Task<(SparkEndpointFactory<GuardedContext> Provider, McpRemoteAgent Agent, SparkUser User)> StartAsync(
        string user, bool local = false, string resource = McpRemoteHost.RemoteUrl,
        Action<SparkIdentityProviderMcpResource>? configureRemote = null, Action<SparkIdentityProviderOptions>? configureIdp = null)
    {
        var clientId = DocumentUrl("claude");
        var documents = new McpEmbeddedHost.DocumentServer().Serve(clientId, McpEmbeddedHost.Document(clientId));
        var provider = McpRemoteHost.Provider(Store, documents, local, configureRemote, configureIdp);
        var created = await McpAccountHost.CreateUserAsync(provider, Name(user));
        return (provider, new McpRemoteAgent(new McpAgentClient(provider.CreateClient(), clientId, LoopbackRedirect), resource), created);
    }

    private static JsonNode Payload(string jwt) => JsonNode.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1]))!;

    private static IReadOnlyList<string> Audiences(JsonNode payload)
        => payload["aud"] is JsonArray array ? [.. array.Select(a => a!.GetValue<string>())] : [payload["aud"]!.GetValue<string>()];

    private static IReadOnlyList<string> Scopes(JsonNode payload) => payload["scope"]!.GetValue<string>().Split(' ');

    /// <summary>The <c>error</c> of a redirect, absolute (to the client) or relative (to the consent page, which has none).</summary>
    private static string? ErrorOf(HttpResponseMessage response)
        => response.Headers.Location is { } location
            ? HttpUtility.ParseQueryString((location.IsAbsoluteUri ? location : new Uri(new Uri("https://x"), location)).Query)["error"]
            : null;

    private static Task<Reply> WhoAmIAsync(HttpClient client, string token)
        => CallAsync(client, "acme_whoami", new { }, McpAgentClient.Bearer(token));

    // ── the provider ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_provider_without_an_mcp_endpoint_signs_an_agent_in_for_another_applications_endpoint()
    {
        var (provider, agent, user) = await StartAsync("remote");
        await using var _ = provider;
        var client = provider.CreateClient();

        // What an agent reads after the resource application's protected-resource document named this issuer.
        var metadata = (await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server"))!;
        metadata["issuer"]!.GetValue<string>().Should().Be(McpRemoteHost.Issuer);
        metadata["client_id_metadata_document_supported"]!.GetValue<bool>().Should().BeTrue();
        metadata["registration_endpoint"]!.GetValue<string>().Should().Be(McpRemoteHost.Issuer + "/connect/register");
        (await client.GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the provider has no /mcp of its own");

        await agent.Agent.SignInAsync(user.Email!);
        var (html, _) = await agent.ConsentPageAsync();
        html.Should().Contain($"data-agent-resource=\"{McpRemoteHost.RemoteUrl}\"").And.Contain(McpRemoteHost.RemoteName, "the user is told where the agent will act");
        html.Should().Contain("data-agent-remote-grant", "and that the types and changes are chosen in that application");
        html.Should().NotContain("name=\"types\"", "the provider cannot see what that application exposes, so it offers no types");
        html.Should().Contain("value=\"write\"", "changes are offered: the endpoint allows them by default");

        var code = await agent.CodeAsync();
        var (status, tokens) = await agent.Agent.RedeemAsync(code, resource: McpRemoteHost.RemoteUrl);
        status.Should().Be(HttpStatusCode.OK, tokens.ToJsonString());

        var access = Payload(tokens["access_token"]!.GetValue<string>());
        Audiences(access).Should().Equal([McpRemoteHost.RemoteUrl], "the token is for that endpoint and nothing else");
        access["iss"]!.GetValue<string>().Should().Be(McpRemoteHost.Issuer);
        access["sub"]!.GetValue<string>().Should().Be(user.Id, "the provider's user id: the key of the login the resource application linked");
        access["client_id"]!.GetValue<string>().Should().Be(agent.Agent.ClientId);
        Scopes(access).Should().Contain("mcp:read").And.NotContain("mcp:write", "changes were not chosen");

        // Refresh rotation keeps the audience.
        var (refreshed, next) = await agent.Agent.RefreshAsync(tokens["refresh_token"]!.GetValue<string>(), McpRemoteHost.RemoteUrl);
        refreshed.Should().Be(HttpStatusCode.OK, next.ToJsonString());
        Audiences(Payload(next["access_token"]!.GetValue<string>())).Should().Equal([McpRemoteHost.RemoteUrl]);

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<AgentGrant>(AgentGrantIds.Application(user.Id!, agent.Agent.ClientId)))
            .Should().BeNull("the agent's grant lives in the resource application, not here");
        (await session.LoadAsync<OidcGrant>(OidcGrantReference.DocumentId(user.Id!, OidcAgentClients.ApplicationId(agent.Agent.ClientId))))!
            .Status.Should().Be("valid", "the OAuth grant here keeps the bookkeeping (revocation, the refresh family)");
        var resource = await session.LoadAsync<OidcResource>(OidcScopeCatalog.ResourceId(OidcMcpResources.RemoteResourceName(McpRemoteHost.RemoteUrl)));
        resource!.Audience.Should().Be(McpRemoteHost.RemoteUrl);
        resource.Scopes.Select(s => s.Name).Should().BeEquivalentTo(["mcp:read", "mcp:write"]);
    }

    [Fact]
    public async Task Changes_chosen_on_the_consent_screen_put_mcp_write_in_the_token()
    {
        var (provider, agent, user) = await StartAsync("write");
        await using var _ = provider;

        var tokens = await agent.SignInAndRedeemAsync(user.Email!, permission: "write");

        Scopes(Payload(tokens["access_token"]!.GetValue<string>())).Should().Contain("mcp:write", "the cap the resource application applies beside its own toggle (D22)");
    }

    [Fact]
    public async Task A_read_only_endpoint_offers_no_changes_and_refuses_a_forged_choice()
    {
        var (provider, agent, user) = await StartAsync("readonly", configureRemote: r => r.Permissions = EAgentPermission.Read);
        await using var _ = provider;
        await agent.Agent.SignInAsync(user.Email!);

        var (html, requestId) = await agent.ConsentPageAsync();
        html.Should().NotContain("value=\"write\"");
        (await agent.Agent.PostConsentAsync(html, requestId, permission: "write")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "a forged POST cannot widen the choice");

        (html, requestId) = await agent.ConsentPageAsync();
        (await agent.Agent.PostConsentAsync(html, requestId, "read", "chosen", "GuardedDoc")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "no types are offered for another application's endpoint, so none can be posted");
    }

    [Fact]
    public async Task A_dynamically_registered_agent_signs_in_for_another_applications_endpoint()
    {
        await using var provider = McpRemoteHost.Provider(Store);
        var user = await McpAccountHost.CreateUserAsync(provider, Name("dcr"));
        var client = provider.CreateClient();

        var registered = await client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Gemini CLI",
            redirect_uris = new[] { "http://localhost:7777/oauth/callback" },
            token_endpoint_auth_method = "none",
        });
        registered.StatusCode.Should().Be(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();
        var agent = new McpRemoteAgent(new McpAgentClient(client, clientId, "http://127.0.0.1:41234/oauth/callback"), McpRemoteHost.RemoteUrl);

        var tokens = await agent.SignInAndRedeemAsync(user.Email!);

        Audiences(Payload(tokens["access_token"]!.GetValue<string>())).Should().Equal([McpRemoteHost.RemoteUrl]);
    }

    [Fact]
    public async Task The_resource_names_the_endpoint_and_anything_else_is_invalid_target()
    {
        var (provider, agent, user) = await StartAsync("target", local: true);
        await using var _ = provider;
        await agent.Agent.SignInAsync(user.Email!);

        ErrorOf(await agent.Agent.AuthorizeAsync(resource: "https://elsewhere.example/mcp"))
            .Should().Be("invalid_target", "an endpoint this server does not sign agents in for never yields a token");
        var two = await agent.Agent.Browser.GetAsync("/connect/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(agent.Agent.ClientId)}&redirect_uri={Uri.EscapeDataString(LoopbackRedirect)}"
            + "&scope=mcp%3Aread&resource=" + Uri.EscapeDataString(McpRemoteHost.RemoteUrl) + "&resource=" + Uri.EscapeDataString(McpRemoteHost.LocalUrl)
            + $"&code_challenge={agent.Agent.Challenge}&code_challenge_method=S256&state=s1");
        ErrorOf(two).Should().Be("invalid_target", "one token, one endpoint: a token valid at two endpoints is the passthrough the spec forbids");

        // Without a resource, the provider's own endpoint is meant, as before other applications' endpoints existed.
        var implied = await agent.Agent.AuthorizeAsync(resource: null);
        implied.Headers.Location!.OriginalString.Should().StartWith("/connect/consent");
        var page = await (await agent.Agent.Browser.GetAsync(implied.Headers.Location!.OriginalString)).Content.ReadAsStringAsync();
        page.Should().Contain("name=\"types\"").And.NotContain("data-agent-resource", "its own endpoint: the types it exposes are offered");

        // A code for the other application's endpoint cannot be redeemed for this one.
        var code = await agent.CodeAsync();
        var (status, body) = await agent.Agent.RedeemAsync(code, resource: McpRemoteHost.LocalUrl);
        status.Should().Be(HttpStatusCode.BadRequest);
        body["error"]!.GetValue<string>().Should().Be("invalid_target");
    }

    [Fact]
    public async Task Without_its_own_endpoint_no_resource_means_the_only_endpoint_and_with_several_none()
    {
        var (single, agent, user) = await StartAsync("single");
        await using (single)
        {
            await agent.Agent.SignInAsync(user.Email!);
            (await agent.Agent.AuthorizeAsync(resource: null)).Headers.Location!.OriginalString
                .Should().StartWith("/connect/consent", "one endpoint served: no resource can only mean it");
        }

        var (several, other, otherUser) = await StartAsync("several", configureIdp: idp => idp.ForMcpResource("https://qna.example/mcp"));
        await using (several)
        {
            await other.Agent.SignInAsync(otherUser.Email!);
            ErrorOf(await other.Agent.AuthorizeAsync(resource: null)).Should().Be("invalid_target", "two endpoints served: the agent must name one");
            (await other.Agent.AuthorizeAsync(resource: "https://qna.example/mcp")).Headers.Location!.OriginalString.Should().StartWith("/connect/consent");
        }
    }

    // ── who accepts the token ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_token_for_the_other_endpoint_is_refused_by_the_providers_own_mcp()
    {
        // The same agent client signs in for both endpoints, so the provider holds an agent grant for it, linked to the
        // same OAuth grant: only the audience tells the two tokens apart.
        var (provider, agent, user) = await StartAsync("own", local: true);
        await using var _ = provider;
        var remote = (await agent.SignInAndRedeemAsync(user.Email!))["access_token"]!.GetValue<string>();
        var local = (await new McpRemoteAgent(agent.Agent, McpRemoteHost.LocalUrl).SignInAndRedeemAsync(user.Email!))["access_token"]!.GetValue<string>();

        var client = provider.CreateClient();
        (await WhoAmIAsync(client, local)).ToolText.Should().Contain($"id={user.Id}", "its own endpoint's token works there");
        var refused = await WhoAmIAsync(client, remote);
        refused.Status.Should().Be(HttpStatusCode.Unauthorized, "a token for another application's endpoint never authenticates this one");
        refused.Response.Headers.WwwAuthenticate.ToString().Should().Contain("invalid_token");
    }

    [Fact]
    public async Task The_resource_application_maps_the_providers_user_through_the_linked_login_and_refuses_an_unlinked_one()
    {
        var (provider, agent, user) = await StartAsync("mapped");
        await using var _p = provider;
        var token = (await agent.SignInAndRedeemAsync(user.Email!))["access_token"]!.GetValue<string>();

        await using var resource = McpRemoteHost.Resource(Store, await McpRemoteHost.ConfigurationOfAsync(provider));
        var linked = await McpAccountHost.CreateUserAsync(resource, Name("hr-linked"));
        var client = resource.CreateClient();

        var unlinked = await WhoAmIAsync(client, token);
        unlinked.Status.Should().Be(HttpStatusCode.Unauthorized, "nobody here linked that account: 401, never anonymous");

        await McpExternalHost.LinkAsync(resource, Store, linked.Id!, McpRemoteHost.LoginProvider, user.Id!);
        var whoami = await WhoAmIAsync(client, token);
        whoami.Status.Should().Be(HttpStatusCode.OK, whoami.Body?.ToJsonString());
        whoami.ToolText.Should().Contain($"id={linked.Id};", "the resource application's own user, found by the linked login, never by email");

        using var session = Store.OpenAsyncSession();
        var grant = await session.LoadAsync<AgentGrant>(AgentGrantIds.ExternalAuthorizationServer(linked.Id!, McpRemoteHost.Issuer, agent.Agent.ClientId));
        grant.Should().NotBeNull("the shadow grant is made in the resource application on the first call (D22)");
        grant!.Permissions.Should().Be(EAgentPermission.Read);
        grant.SecurityStamp.Should().Be((await session.LoadAsync<SparkUser>(linked.Id!))!.SecurityStamp, "stamped like every other agent grant (owner, 2026-10-12)");
    }

    [Fact]
    public async Task Another_applications_own_authorization_server_refuses_the_token()
    {
        var (provider, agent, user) = await StartAsync("fleet");
        await using var _p = provider;
        var token = (await agent.SignInAndRedeemAsync(user.Email!))["access_token"]!.GetValue<string>();

        await using var fleet = McpRemoteHost.OtherEmbeddedServer(second.Store, McpRemoteHost.NewSigningKeyFile());
        var refused = await WhoAmIAsync(fleet.CreateClient(), token);

        refused.Status.Should().Be(HttpStatusCode.Unauthorized, "another issuer, another key, another audience: never this endpoint's token, never anonymous");
    }

    // ── configuration ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://hr.example/mcp")]
    [InlineData("https://hr.example/mcp?tenant=1")]
    [InlineData("https://hr.example/mcp#x")]
    [InlineData("https://user@hr.example/mcp")]
    [InlineData("/mcp")]
    public void A_malformed_endpoint_url_is_refused(string url)
    {
        var act = () => OidcMcpResources.RemoteAudience(url);

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("canonical URL");
    }

    [Fact]
    public void An_endpoint_url_is_taken_in_its_canonical_form()
    {
        OidcMcpResources.RemoteAudience("https://hr.example/mcp/").Should().Be("https://hr.example/mcp", "the Mcp package drops a trailing slash too");
        OidcMcpResources.RemoteAudience("http://localhost:5006/mcp").Should().Be("http://localhost:5006/mcp", "plain http on a loopback host, for local development");
    }

    [Fact]
    public void A_duplicate_endpoint_refuses_startup()
    {
        var act = () => McpRemoteHost.Provider(Store, configureIdp: idp => idp.ForMcpResource(McpRemoteHost.RemoteUrl + "/")).CreateClient();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("listed twice");
    }

    [Fact]
    public void ForMcp_without_the_mcp_server_still_refuses_startup()
    {
        var act = () => new SparkEndpointFactory<GuardedContext>(
            Store,
            McpAccountHost.Models(),
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>();
                spark.AddIdentityProvider(idp =>
                {
                    idp.Issuer = McpRemoteHost.Issuer;
                    idp.SigningKeyPath = OidcTestHost.CopyOfSharedSigningKey();
                    idp.ForMcp();
                });
            },
            environment: "Development",
            security: SparkTestSecurity.FromJson(McpAccountHost.Security())).CreateClient();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("spark.AddMcp");
    }
}
