using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Agents;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Authorization.ResourceServer;
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
/// A Spark identity provider as another application's external authorization server (remote resources, 2026-10-12):
/// <see cref="Provider"/> signs agents in for <see cref="RemoteUrl"/> (<c>idp.ForMcpResource</c>, the SparkId of the
/// demo), optionally for its own <c>/mcp</c> too; <see cref="Resource"/> is the application at that URL, which accepts the
/// provider's tokens through <c>AddExternalAgentAuthorizationServer</c> bound to its <see cref="LoginProvider"/> sign-in
/// (HR of the demo); <see cref="OtherEmbeddedServer"/> is a third application with an authorization server of its own
/// (Fleet of the demo), in its own database with its own signing key.
/// </summary>
internal static class McpRemoteHost
{
    /// <summary>The provider's issuer.</summary>
    public const string Issuer = "https://id.example";

    /// <summary>The other application's MCP endpoint, which the provider signs agents in for.</summary>
    public const string RemoteUrl = "https://hr.example/mcp";

    /// <summary>The provider's own MCP endpoint, when it has one.</summary>
    public const string LocalUrl = "https://id.example/mcp";

    /// <summary>The resource application's sign-in provider for the identity provider.</summary>
    public const string LoginProvider = "SparkId";

    public const string RemoteName = "Acme HR";

    public static SparkEndpointFactory<GuardedContext> Provider(
        IDocumentStore store,
        McpEmbeddedHost.DocumentServer? documents = null,
        bool local = false,
        Action<SparkIdentityProviderMcpResource>? configureRemote = null,
        Action<SparkIdentityProviderOptions>? configureIdp = null)
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
                    if (local)
                        idp.ForMcp();
                    idp.ForMcpResource(RemoteUrl, r =>
                    {
                        r.DisplayName = RemoteName;
                        configureRemote?.Invoke(r);
                    });
                    configureIdp?.Invoke(idp);
                });
                if (local)
                {
                    spark.AddMcp(mcp =>
                    {
                        mcp.ServerName = "Acme Identity";
                        mcp.ToolPrefix = "acme";
                        mcp.CanonicalUrl = LocalUrl;
                        mcp.AgentPermissions = _ => EAgentPermission.Read | EAgentPermission.Write;
                        mcp.AddTools<AcmeTestTools>();
                    });
                }
            },
            environment: "Development",
            security: SparkTestSecurity.FromJson(McpAccountHost.Security()));

    /// <summary>The application at <see cref="RemoteUrl"/>: setup 2 bound to the provider, validating with <paramref name="provider"/>'s keys.</summary>
    public static SparkEndpointFactory<GuardedContext> Resource(IDocumentStore store, OpenIdConnectConfiguration provider)
        => new(
            store,
            McpAccountHost.Models(),
            configureServices: services =>
            {
                // The provider's published keys in place of its discovery document.
                services.Configure<JwtBearerOptions>(ExternalAgentServer.TokenScheme, o => o.Configuration = provider);
                services.AddAuthentication().AddOAuth(LoginProvider, LoginProvider, o =>
                {
                    o.ClientId = "hr";
                    o.ClientSecret = "secret";
                    o.AuthorizationEndpoint = Issuer + "/connect/authorize";
                    o.TokenEndpoint = Issuer + "/connect/token";
                    o.CallbackPath = "/signin-sparkid";
                });
            },
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>(auth =>
                {
                    auth.LocalCredentials = SparkLocalCredentials.Full;
                    auth.AllowUnconfirmedRegistration = true;
                });
                spark.AddExternalScheme(LoginProvider, SparkExternalProviderPolicy.Default);
                spark.AddExternalAgentAuthorizationServer(Issuer, LoginProvider);
                spark.AddMcp(mcp =>
                {
                    mcp.ServerName = RemoteName;
                    mcp.ToolPrefix = "acme";
                    mcp.CanonicalUrl = RemoteUrl;
                    mcp.AgentPermissions = _ => EAgentPermission.Read | EAgentPermission.Write;
                    mcp.AddTools<AcmeTestTools>();
                });
            },
            security: SparkTestSecurity.FromJson(McpAccountHost.Security()));

    /// <summary>An application with an authorization server of its own, at <see cref="McpEmbeddedHost.Issuer"/> with its own key.</summary>
    public static SparkEndpointFactory<GuardedContext> OtherEmbeddedServer(IDocumentStore store, string signingKeyPath)
        => new(
            store,
            McpAccountHost.Models(),
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>(auth =>
                {
                    auth.LocalCredentials = SparkLocalCredentials.Full;
                    auth.AllowUnconfirmedRegistration = true;
                });
                spark.AddIdentityProvider(idp =>
                {
                    idp.Issuer = McpEmbeddedHost.Issuer;
                    idp.SigningKeyPath = signingKeyPath;
                    idp.ForMcp();
                });
                spark.AddMcp(mcp =>
                {
                    mcp.ServerName = "Acme Fleet";
                    mcp.ToolPrefix = "acme";
                    mcp.CanonicalUrl = McpTestHost.CanonicalUrl;
                    mcp.AgentPermissions = _ => EAgentPermission.Read;
                    mcp.AddTools<AcmeTestTools>();
                });
            },
            environment: "Development",
            security: SparkTestSecurity.FromJson(McpAccountHost.Security()));

    /// <summary>The provider's signing keys as its JWKS publishes them: what the resource application's validation reads.</summary>
    public static async Task<OpenIdConnectConfiguration> ConfigurationOfAsync(SparkEndpointFactory<GuardedContext> provider)
    {
        var jwks = await provider.CreateClient().GetStringAsync("/.well-known/jwks");
        var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
        foreach (var key in new JsonWebKeySet(jwks).GetSigningKeys())
            configuration.SigningKeys.Add(key);
        return configuration;
    }

    /// <summary>A signing key of its own (the shape the provider imports), for a server that must not share the provider's.</summary>
    public static string NewSigningKeyFile()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var p = rsa.ExportParameters(includePrivateParameters: true);
        static string B64(byte[] bytes) => Base64UrlEncoder.Encode(bytes);
        var path = Path.Combine(Path.GetTempPath(), "spark-oidc-test-other-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["N"] = B64(p.Modulus!), ["E"] = B64(p.Exponent!), ["D"] = B64(p.D!), ["P"] = B64(p.P!),
            ["Q"] = B64(p.Q!), ["DP"] = B64(p.DP!), ["DQ"] = B64(p.DQ!), ["QI"] = B64(p.InverseQ!),
        }));
        return path;
    }
}

/// <summary>
/// <see cref="McpAgentClient"/>'s steps for an endpoint other than <see cref="McpTestHost.CanonicalUrl"/>: authorize with
/// <c>resource</c> = <see cref="Resource"/>, the consent page, the code (with <see cref="Issuer"/> as RFC 9207 <c>iss</c>),
/// and the exchange naming the same resource.
/// </summary>
internal sealed class McpRemoteAgent(McpAgentClient agent, string resource, string issuer = McpRemoteHost.Issuer)
{
    public McpAgentClient Agent => agent;
    public string Resource => resource;

    public async Task<(string Html, string RequestId)> ConsentPageAsync()
    {
        var authorize = await agent.AuthorizeAsync(resource: resource);
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect, await authorize.Content.ReadAsStringAsync());
        var location = authorize.Headers.Location!.OriginalString;
        location.Should().StartWith("/connect/consent?request_id=", "an agent sign-in always asks the user");
        var page = await agent.Browser.GetAsync(location);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await page.Content.ReadAsStringAsync(), HttpUtility.ParseQueryString(new Uri("https://x" + location).Query)["request_id"]!);
    }

    public async Task<string> CodeAsync(string permission = "read")
    {
        var (html, requestId) = await ConsentPageAsync();
        var consent = await agent.PostConsentAsync(html, requestId, permission);
        consent.StatusCode.Should().Be(HttpStatusCode.Redirect, await consent.Content.ReadAsStringAsync());
        var query = HttpUtility.ParseQueryString(consent.Headers.Location!.Query);
        query["iss"].Should().Be(issuer, "RFC 9207 iss stays on the response");
        return query["code"]!;
    }

    public async Task<JsonNode> SignInAndRedeemAsync(string email, string permission = "read")
    {
        await agent.SignInAsync(email);
        var code = await CodeAsync(permission);
        var (status, body) = await agent.RedeemAsync(code, resource: resource);
        status.Should().Be(HttpStatusCode.OK, body.ToJsonString());
        return body;
    }
}
