using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.ResourceServer;
using MintPlayer.Spark.Mcp;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Mcp._Infrastructure;

/// <summary>
/// Setup 2 for real (MCP PRD D4, D22; plan M3c): the Authorization package with an external authorization server bound
/// to the sign-in provider <see cref="Provider"/>, and the MCP server, with <see cref="McpAccountHost"/>'s fixtures and
/// rights. The external server is <see cref="TestIssuer"/>: in-memory keys, no network.
/// </summary>
internal static class McpExternalHost
{
    /// <summary>The sign-in provider bound to <see cref="TestIssuer.Issuer"/>.</summary>
    public const string Provider = "Corp";

    /// <summary>Another sign-in provider of the same application, bound to nothing.</summary>
    public const string OtherProvider = "Partner";

    public static SparkEndpointFactory<GuardedContext> Create(
        IDocumentStore store,
        TestIssuer issuer,
        Action<SparkExternalAgentServerOptions>? configureExternal = null,
        Action<SparkMcpOptions>? configureMcp = null,
        string loginProvider = Provider,
        IntrospectionStub? introspection = null,
        bool developersEdit = false)
        => new(
            store,
            McpAccountHost.Models(),
            configureServices: services =>
            {
                // The issuer's keys in place of its discovery document (JWT validation).
                services.Configure<JwtBearerOptions>(ExternalAgentServer.TokenScheme, o => o.Configuration = issuer.Configuration);
                if (introspection is not null)
                    services.AddHttpClient(SparkResourceServerOptions.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => introspection);

                foreach (var provider in new[] { Provider, OtherProvider })
                {
                    services.AddAuthentication().AddOAuth(provider, provider, o =>
                    {
                        o.ClientId = "website";
                        o.ClientSecret = "secret";
                        o.AuthorizationEndpoint = issuer.Issuer + "/authorize";
                        o.TokenEndpoint = issuer.Issuer + "/token";
                        o.CallbackPath = "/signin-" + provider.ToLowerInvariant();
                    });
                }
            },
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>(auth =>
                {
                    auth.LocalCredentials = SparkLocalCredentials.Full;
                    auth.AllowUnconfirmedRegistration = true;
                });
                spark.AddExternalScheme(Provider, SparkExternalProviderPolicy.Default);
                spark.AddExternalScheme(OtherProvider, SparkExternalProviderPolicy.Default);
                spark.AddExternalAgentAuthorizationServer(issuer.Issuer, loginProvider, o =>
                {
                    if (introspection is not null)
                    {
                        o.UseIntrospection = true;
                        o.IntrospectionClientId = "acme-mcp";
                        o.IntrospectionClientSecret = "introspection-secret";
                        o.IntrospectionCacheDuration = TimeSpan.Zero;
                    }
                    configureExternal?.Invoke(o);
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
            security: SparkTestSecurity.FromJson(McpAccountHost.Security(developersEdit)));

    /// <summary>
    /// Links an external login to the user, as a sign-in at the website through <paramref name="provider"/> would, and
    /// waits until Identity's login lookup sees it (a dynamic RavenDB index).
    /// </summary>
    public static async Task LinkAsync(SparkEndpointFactory<GuardedContext> factory, IDocumentStore store, string userId, string provider, string subject)
    {
        await McpAccountHost.ChangeUserAsync(factory, userId, (users, user) => users.AddLoginAsync(user, new UserLoginInfo(provider, subject, provider)));

        using var session = store.OpenAsyncSession();
        await session.Query<SparkUser>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(u => u.Logins.Any(l => l.ProviderKey == subject))
            .ToListAsync();
    }

    public static Action<HttpRequestMessage> Token(string token) => r => r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

/// <summary>An external authorization server's signing side: an RSA key in memory and tokens shaped like its access tokens.</summary>
internal sealed class TestIssuer(string issuer = TestIssuer.DefaultIssuer)
{
    public const string DefaultIssuer = "https://login.corp.example";
    public const string ClientId = "agent-cli";

    public string Issuer { get; } = issuer;

    public RsaSecurityKey Key { get; } = new(RSA.Create(2048)) { KeyId = Guid.NewGuid().ToString("N") };

    public OpenIdConnectConfiguration Configuration
    {
        get
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(Key);
            return configuration;
        }
    }

    /// <summary>An access token; every part can be bent for the adversarial cases.</summary>
    public string Token(
        string subject,
        string scope = "mcp:read",
        string? audience = McpTestHost.CanonicalUrl,
        string clientId = ClientId,
        string type = "at+jwt",
        string? issuer = null,
        SecurityKey? key = null,
        string algorithm = SecurityAlgorithms.RsaSha256,
        DateTime? notBefore = null,
        DateTime? expires = null,
        IDictionary<string, object>? claims = null)
    {
        var now = DateTime.UtcNow;
        var all = new Dictionary<string, object> { ["sub"] = subject, ["azp"] = clientId, ["scope"] = scope };
        foreach (var (name, value) in claims ?? new Dictionary<string, object>())
            all[name] = value;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer,
            Audience = audience,
            Claims = all,
            TokenType = type,
            NotBefore = notBefore ?? now.AddMinutes(-1),
            IssuedAt = notBefore ?? now.AddMinutes(-1),
            Expires = expires ?? now.AddMinutes(30),
            SigningCredentials = new SigningCredentials(key ?? Key, algorithm),
        });
    }

    /// <summary>The same claims, unsigned (<c>alg: none</c>).</summary>
    public string UnsignedToken(string subject)
    {
        static string Part(object value) => Base64UrlEncoder.Encode(JsonSerializer.Serialize(value));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Part(new { alg = "none", typ = "at+jwt" }) + "."
            + Part(new { iss = Issuer, aud = McpTestHost.CanonicalUrl, sub = subject, azp = ClientId, scope = "mcp:read", iat = now, nbf = now, exp = now + 1800 })
            + ".";
    }
}

/// <summary>
/// An external server that hands out opaque tokens and answers RFC 7662 introspection for them, with its discovery
/// document. Installed as the primary handler of <see cref="SparkResourceServerOptions.HttpClientName"/>, which serves
/// both the discovery fetch and the introspection call.
/// </summary>
internal sealed class IntrospectionStub : HttpMessageHandler
{
    public const string Issuer = "https://opaque.corp.example";

    private readonly Dictionary<string, JsonObject> active = new(StringComparer.Ordinal);

    /// <summary>An opaque token that introspects as active with these members.</summary>
    public string Issue(string subject, string scope = "mcp:read", string audience = McpTestHost.CanonicalUrl, string issuer = Issuer)
    {
        var token = "opaque-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        active[token] = new JsonObject
        {
            ["active"] = true,
            ["iss"] = issuer,
            ["aud"] = audience,
            ["sub"] = subject,
            ["client_id"] = TestIssuer.ClientId,
            ["scope"] = scope,
            ["token_type"] = "access_token",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds(),
        };
        return token;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/.well-known/openid-configuration")
        {
            return Json(new JsonObject
            {
                ["issuer"] = Issuer,
                ["introspection_endpoint"] = Issuer + "/introspect",
                ["jwks_uri"] = Issuer + "/jwks",
            });
        }
        if (path == "/jwks")
            return Json(new JsonObject { ["keys"] = new JsonArray() });
        if (path == "/introspect" && request.Method == HttpMethod.Post)
        {
            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("acme-mcp:introspection-secret"));
            if (request.Headers.Authorization is not { Scheme: "Basic" } basic || basic.Parameter != expected)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            var token = Uri.UnescapeDataString(form.Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == "token")[1]);
            return Json(active.TryGetValue(token, out var answer) ? (JsonObject)answer.DeepClone() : new JsonObject { ["active"] = false });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(JsonObject body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
