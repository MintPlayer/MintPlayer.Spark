using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeCoverage.Tests.Mcp._Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CodeCoverage.Tests.Mcp;

/// <summary>
/// The embedded authorization server runs inside CodeCoverage (MCP PRD D24, setup 3), next to the app's only sign-in,
/// GitHub. An agent's sign-in starts at <c>/connect/authorize</c>, sends the browser to <c>/connect/login</c>, and from there
/// into the website's own GitHub sign-in; the consent screen then needs the signed-in user (spike S2's flow, in this app).
/// </summary>
[Collection(CoverageMcpHostCollection.Name)]
public partial class CoverageMcpSignInTests(CoverageMcpHostFixture fixture)
{
    private const string Origin = "http://localhost";
    private const string Canonical = Origin + "/mcp";

    [Fact]
    public async Task The_resource_and_its_embedded_authorization_server_are_published()
    {
        using var client = Client();

        var resource = (await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-protected-resource/mcp"))!;
        resource["resource"]!.GetValue<string>().Should().Be(Canonical, "the canonical URL follows the public origin, never the Host header");
        resource["authorization_servers"]!.AsArray().Select(s => s!.GetValue<string>()).Should().Equal(Origin);

        var server = (await client.GetFromJsonAsync<JsonObject>("/.well-known/oauth-authorization-server"))!;
        server["issuer"]!.GetValue<string>().Should().Be(Origin);
        server["client_id_metadata_document_supported"]!.GetValue<bool>().Should().BeTrue();
        server["code_challenge_methods_supported"]!.AsArray().Select(m => m!.GetValue<string>()).Should().Contain("S256");
        var scopes = server["scopes_supported"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        scopes.Should().Contain("mcp:read");
        scopes.Should().Contain("mcp:write");

        // Without a token, a call that needs one carries the challenge pointing at that document.
        var refused = await McpWire.CallAsync(client, "codecoverage_summary", new { repository = "github/acme/secret" });
        refused.Status.Should().Be(HttpStatusCode.Unauthorized);
        refused.Response.Headers.WwwAuthenticate.ToString().Should().Contain(Origin + "/.well-known/oauth-protected-resource/mcp");
    }

    [Fact]
    public async Task An_agent_sign_in_goes_through_the_apps_own_GitHub_sign_in()
    {
        using var client = Client();

        // A native client registers itself anonymously, with a loopback redirect (D5), as Gemini CLI does.
        var registered = await client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "Test agent",
            redirect_uris = new[] { "http://127.0.0.1:33418/callback" },
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
        });
        registered.StatusCode.Should().Be(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync());
        var clientId = (await registered.Content.ReadFromJsonAsync<JsonObject>())!["client_id"]!.GetValue<string>();

        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authorize = await client.GetAsync("/connect/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString("http://127.0.0.1:33418/callback")}"
            + $"&scope={Uri.EscapeDataString("mcp:read offline_access")}&resource={Uri.EscapeDataString(Canonical)}"
            + $"&code_challenge={challenge}&code_challenge_method=S256&state=s1");

        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect, await authorize.Content.ReadAsStringAsync());
        var login = authorize.Headers.Location!.OriginalString;
        login.Should().StartWith("/connect/login", "nobody is signed in yet: the user signs in first, then consents");

        // The login page offers the app's own sign-in: GitHub, the only one this app registers.
        var page = await client.GetAsync(login);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var href = WebUtility.HtmlDecode(ExternalLogin().Match(await page.Content.ReadAsStringAsync()).Groups["href"].Value);
        href.Should().StartWith("/spark/auth/external-login?").And.Contain("provider=GitHub");

        // Following it is the website's GitHub sign-in, which comes back to the pending authorization afterwards.
        var github = await client.GetAsync(href);
        github.StatusCode.Should().Be(HttpStatusCode.Redirect);
        github.Headers.Location!.ToString().Should().StartWith("https://github.com/login/oauth/authorize");
    }

    private HttpClient Client() => fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [GeneratedRegex("<a class=\"btn btn-external\" href=\"(?<href>[^\"]+)\"")]
    private static partial Regex ExternalLogin();
}
