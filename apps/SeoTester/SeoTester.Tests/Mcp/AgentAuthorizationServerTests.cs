using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests.Mcp;

/// <summary>
/// SeoTester hosts the identity provider only so that its agents can sign in (<c>idp.ForMcp().AgentsOnly()</c>; MCP PRD
/// decision log, "IdP surface for MCP-only apps"): what a full provider maps beside the agents' surface answers 404, never the
/// SPA, both metadata documents advertise only what is mapped, and an agent still registers itself and is sent to the
/// website's own sign-in.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class AgentAuthorizationServerTests(SeoTesterHostFixture host)
{
    private const string Origin = "http://localhost";
    private const string Redirect = "http://127.0.0.1:33418/callback";

    [Fact]
    public async Task SeoTester_maps_only_what_its_agents_need()
    {
        using var client = host.CreateClient();

        var mapped = new List<string>();
        foreach (var (method, path) in NotForAgents)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT")
                request.Content = new FormUrlEncodedContent([]);
            using var response = await client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.NotFound)
                mapped.Add($"{method} {path} answered {(int)response.StatusCode}");
        }
        mapped.Should().BeEmpty("SeoTester's provider serves agents only");

        var server = (await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server"))!;
        server["grant_types_supported"]!.AsArray().Select(g => g!.GetValue<string>()).Should().Equal("authorization_code", "refresh_token");
        server["registration_endpoint"]!.GetValue<string>().Should().Be(Origin + "/connect/register");
        server["revocation_endpoint"]!.GetValue<string>().Should().Be(Origin + "/connect/revoke");

        var discovery = (await client.GetFromJsonAsync<JsonObject>("/.well-known/openid-configuration"))!;
        discovery["grant_types_supported"]!.AsArray().Select(g => g!.GetValue<string>()).Should().Equal("authorization_code", "refresh_token");
        discovery.Select(p => p.Key).Should().NotIntersectWith([
            "userinfo_endpoint", "end_session_endpoint", "introspection_endpoint", "pushed_authorization_request_endpoint",
            "device_authorization_endpoint", "backchannel_logout_supported",
        ]);
    }

    [Fact]
    public async Task An_agent_registers_itself_and_is_sent_to_the_websites_own_sign_in()
    {
        using var client = host.CreateClient();

        // A native client registers itself anonymously, with a loopback redirect (D5), as Gemini CLI does.
        var registered = await client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Test agent",
            redirect_uris = new[] { Redirect },
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
        });
        registered.StatusCode.Should().Be(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();

        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var authorize = await client.GetAsync("/connect/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(Redirect)}"
            + $"&scope={Uri.EscapeDataString("mcp:read offline_access")}&resource={Uri.EscapeDataString(Origin + "/mcp")}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=s1");

        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect, await authorize.Content.ReadAsStringAsync());
        authorize.Headers.Location!.OriginalString.Should().StartWith("/connect/login", "nobody is signed in yet: the user signs in first, then consents");
    }

    /// <summary>What a full provider maps beside the agents' surface.</summary>
    private static readonly (string Method, string Path)[] NotForAgents =
    [
        ("POST", "/connect/device_authorization"), ("GET", "/connect/device"), ("POST", "/connect/device"), ("POST", "/connect/par"),
        ("POST", "/connect/introspect"), ("GET", "/connect/userinfo"), ("POST", "/connect/userinfo"), ("GET", "/connect/logout"),
        ("GET", "/connect/applications"), ("POST", "/connect/applications/revoke"), ("POST", "/connect/applications/revoke-scope"),
        ("GET", "/connect/register/some-client"), ("PUT", "/connect/register/some-client"), ("DELETE", "/connect/register/some-client"),
        ("GET", "/spark/identity-provider/developer"), ("POST", "/spark/identity-provider/developer"),
        ("POST", "/spark/identity-provider/developer/registration-token"), ("POST", "/spark/identity-provider/invitations/accept"),
        ("GET", "/spark/identity-provider/applications"), ("POST", "/spark/identity-provider/applications/withdraw"),
        ("GET", "/spark/identity-provider/admin/keys"), ("POST", "/spark/identity-provider/admin/keys/rotate"),
    ];
}
