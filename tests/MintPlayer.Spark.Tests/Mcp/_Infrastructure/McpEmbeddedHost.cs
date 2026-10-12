using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.IdentityProvider.Mcp;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using MintPlayer.Spark.Tests.IdentityProvider;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Mcp._Infrastructure;

/// <summary>
/// Setup 3 for real (MCP PRD D4, D5; plan M3b): the Authorization package, the IdentityProvider with
/// <c>idp.ForMcp()</c> and the MCP server in one host, with <see cref="McpAccountHost"/>'s fixtures and rights.
/// Metadata documents are served by <see cref="DocumentServer"/>, the stub behind the SSRF-safe client's guards.
/// </summary>
internal static class McpEmbeddedHost
{
    public const string Issuer = "https://acme.example";

    public static SparkEndpointFactory<GuardedContext> Create(
        IDocumentStore store,
        DocumentServer? documents = null,
        Action<SparkMcpOptions>? configureMcp = null,
        Action<SparkIdentityProviderMcpOptions>? configureIdp = null)
        => new(
            store,
            McpAccountHost.Models(),
            configureServices: services =>
            {
                if (documents is not null)
                    services.AddHttpClient(OidcClientMetadataDocuments.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => documents);
            },
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>(auth =>
                {
                    auth.LocalCredentials = SparkLocalCredentials.Full;
                    auth.AllowUnconfirmedRegistration = true;
                });
                spark.AddIdentityProvider(idp =>
                {
                    idp.Issuer = Issuer;
                    idp.SigningKeyPath = OidcTestHost.CopyOfSharedSigningKey();
                    idp.ForMcp(configureIdp);
                });
                spark.AddMcp(mcp =>
                {
                    mcp.ServerName = "Acme Test";
                    mcp.ToolPrefix = "acme";
                    mcp.CanonicalUrl = McpTestHost.CanonicalUrl;
                    mcp.AgentPermissions = _ => EAgentPermission.Read | EAgentPermission.Write;
                    mcp.AddTools<AcmeTestTools>();
                    configureMcp?.Invoke(mcp);
                });
            },
            environment: "Development",
            security: SparkTestSecurity.FromJson(McpAccountHost.Security()));

    /// <summary>The stub metadata-document server: answers the registered URLs, 404 for anything else.</summary>
    public sealed class DocumentServer : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> documents = new(StringComparer.Ordinal);

        public int Fetches { get; private set; }

        public DocumentServer Serve(string url, string json)
        {
            documents[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            return this;
        }

        public DocumentServer Serve(string url, Func<HttpResponseMessage> response)
        {
            documents[url] = response;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(documents.TryGetValue(request.RequestUri!.ToString(), out var response)
                ? response()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>A Client ID Metadata Document as Claude Code publishes one: portless loopback redirects, public client.</summary>
    public static string Document(string clientId, string name = "Claude Code", params string[] redirects)
        => new JsonObject
        {
            ["client_id"] = clientId,
            ["client_name"] = name,
            ["redirect_uris"] = new JsonArray([.. (redirects.Length == 0 ? ["http://localhost/callback", "http://127.0.0.1/callback"] : redirects).Select(r => (JsonNode)r!)]),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        }.ToJsonString();
}

/// <summary>
/// A scripted public MCP client (the spike's approach, plan M0): the browser that signs in and consents, and the
/// agent that runs code + PKCE (S256) with <c>resource</c> and calls <c>/mcp</c>.
/// </summary>
internal sealed class McpAgentClient(HttpClient client, string clientId, string redirectUri)
{
    public string ClientId => clientId;
    public string RedirectUri => redirectUri;

    public OidcTestHost.Browser Browser { get; } = new(client);

    public string Verifier { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string Challenge => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Signs the user in at the provider's own sign-in page.</summary>
    public async Task SignInAsync(string email, string password = McpAccountHost.Password)
    {
        var page = await Browser.GetAsync("/connect/login?returnUrl=%2F");
        var token = OidcTestHost.AntiforgeryTokenFrom(await page.Content.ReadAsStringAsync());
        var response = await Browser.PostFormAsync("/connect/login", new Dictionary<string, string>
        {
            ["email"] = email, ["password"] = password, ["returnUrl"] = "/", ["__RequestVerificationToken"] = token,
        });
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the password sign-in succeeds");
    }

    public Task<HttpResponseMessage> AuthorizeAsync(string? scope = "mcp:read mcp:write offline_access", string? resource = McpTestHost.CanonicalUrl, string? redirect = null)
        => Browser.GetAsync("/connect/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect ?? redirectUri)}"
            + (scope is null ? "" : $"&scope={Uri.EscapeDataString(scope)}")
            + (resource is null ? "" : $"&resource={Uri.EscapeDataString(resource)}")
            + $"&code_challenge={Challenge}&code_challenge_method=S256&state=s1");

    /// <summary>Authorize, then the consent page; answers the consent page's HTML and its request id.</summary>
    public async Task<(string Html, string RequestId, HttpResponseMessage Page)> ConsentPageAsync(string? redirect = null)
    {
        var authorize = await AuthorizeAsync(redirect: redirect);
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect, await authorize.Content.ReadAsStringAsync());
        var location = authorize.Headers.Location!.OriginalString;
        location.Should().StartWith("/connect/consent?request_id=", "an agent sign-in always asks the user");
        var page = await Browser.GetAsync(location);
        return (await page.Content.ReadAsStringAsync(), HttpUtility.ParseQueryString(new Uri("https://x" + location).Query)["request_id"]!, page);
    }

    public async Task<HttpResponseMessage> PostConsentAsync(string html, string requestId, string permission = "read", string typesMode = "all", params string[] types)
    {
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("request_id", requestId), new("decision", "allow"), new("types_mode", typesMode), new("permission", permission),
            new("__RequestVerificationToken", OidcTestHost.AntiforgeryTokenFrom(html)),
        };
        pairs.AddRange(types.Select(t => new KeyValuePair<string, string>("types", t)));
        return await Browser.PostRawAsync("/connect/consent", pairs);
    }

    /// <summary>The whole browser part: authorize, consent, and the code from the redirect.</summary>
    public async Task<string> CodeAsync(string permission = "read", string? redirect = null)
    {
        var (html, requestId, _) = await ConsentPageAsync(redirect);
        var consent = await PostConsentAsync(html, requestId, permission);
        consent.StatusCode.Should().Be(HttpStatusCode.Redirect, await consent.Content.ReadAsStringAsync());
        var query = HttpUtility.ParseQueryString(consent.Headers.Location!.Query);
        query["iss"].Should().Be(McpEmbeddedHost.Issuer, "RFC 9207 iss stays on the response");
        return query["code"]!;
    }

    public async Task<(HttpStatusCode Status, JsonNode Body)> TokenAsync(IDictionary<string, string> form)
    {
        var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    public Task<(HttpStatusCode Status, JsonNode Body)> RedeemAsync(string code, string? resource = McpTestHost.CanonicalUrl, string? redirect = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect ?? redirectUri,
            ["client_id"] = clientId, ["code_verifier"] = Verifier,
        };
        if (resource is not null)
            form["resource"] = resource;
        return TokenAsync(form);
    }

    public Task<(HttpStatusCode Status, JsonNode Body)> RefreshAsync(string refreshToken, string? resource = null)
    {
        var form = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = clientId };
        if (resource is not null)
            form["resource"] = resource;
        return TokenAsync(form);
    }

    /// <summary>Sign-in, consent and code exchange; answers the token response.</summary>
    public async Task<JsonNode> SignInAndRedeemAsync(string email, string permission = "read", string? redirect = null)
    {
        await SignInAsync(email);
        var code = await CodeAsync(permission, redirect);
        var (status, body) = await RedeemAsync(code, redirect: redirect);
        status.Should().Be(HttpStatusCode.OK, body.ToJsonString());
        return body;
    }

    public static Action<HttpRequestMessage> Bearer(string token) => r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
