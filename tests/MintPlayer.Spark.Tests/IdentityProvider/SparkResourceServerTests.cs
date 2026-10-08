using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.ResourceServer;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// <c>spark.AddSparkResourceServer(...)</c> (PRD D8, I12) against the in-process identity provider: a tiny
/// resource-server host whose JwtBearer back channel (and introspection client) is the provider's
/// <see cref="TestServer"/> handler, so tokens are minted by the real token endpoint and validated with the
/// keys the real JWKS publishes.
/// </summary>
public class SparkResourceServerTests(OidcSharedHost host) : OidcTestHost(host), IClassFixture<OidcSharedHost>
{
    private const string Secret = "s3cret-value-for-tests";
    private const string ItemsUrl = "http://localhost/api/items";

    /// <summary>The API resource of this case, and therefore the audience of its tokens.</summary>
    private string Api => ClientId("inventory");

    private string ReadScope => Api + ".read";

    private HttpMessageHandler IdpHandler => ((TestServer)Factory.GetService<IServer>()).CreateHandler();

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// A resource server with <c>GET /api/items</c> (needs <c>{api}.read</c>) and <c>GET /api/items/write</c>
    /// (needs <c>{api}.write</c>).
    /// </summary>
    private async Task<IHost> StartResourceServerAsync(string audience, Action<SparkResourceServerOptions>? configure = null)
    {
        var idp = IdpHandler;
        var read = ReadScope;
        var write = Api + ".write";

        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();

                    var spark = new MintPlayer.Spark.SparkBuilder(services, null);
                    services.AddSingleton(spark.Registry);
                    spark.AddSparkResourceServer(Issuer, audience, configure);

                    // Discovery and JWKS from the in-process provider. Configure runs before the JwtBearer
                    // post-configuration that builds the ConfigurationManager from this handler.
                    services.Configure<JwtBearerOptions>(SparkJwtBearerExtensions.Scheme, o => o.BackchannelHttpHandler = idp);
                    services.AddHttpClient(nameof(SparkIntrospectionHandler)).ConfigurePrimaryHttpMessageHandler(() => IdpHandler);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/api/items", () => "items").RequireScope(read);
                        endpoints.MapGet("/api/items/write", () => "written").RequireScope(write);
                    });
                }))
            .StartAsync();
    }

    /// <summary>Runs the code flow for <c>openid {api}.read</c> and returns the token response.</summary>
    private async Task<(OidcApplication App, JsonElement Body)> IssueAsync(DpopTestKey? dpopKey = null)
    {
        var app = await SeedApplicationAsync(ClientId("webapp"), allowedScopes: ["openid", ReadScope]);
        await SeedUserAsync(UserEmail("alice"));
        var code = await ObtainCodeAsync(app, UserEmail("alice"), ["openid", ReadScope]);

        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = app.ClientId,
                ["client_secret"] = Secret,
                ["code"] = code,
                ["redirect_uri"] = app.RedirectUris[0],
            }),
        };
        if (dpopKey is not null)
            request.Headers.TryAddWithoutValidation("DPoP", dpopKey.Proof("POST", Issuer + "/connect/token"));

        var response = await Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (app, await BodyAsync(response));
    }

    private static Task<HttpResponseMessage> CallAsync(IHost rs, string path, string scheme, string token, string? proof = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        if (proof is not null)
            request.Headers.TryAddWithoutValidation("DPoP", proof);
        return rs.GetTestClient().SendAsync(request);
    }

    // ---------- JWT validation ----------

    [Fact]
    public async Task An_access_token_carrying_the_scope_is_accepted()
    {
        var (_, body) = await IssueAsync();
        using var rs = await StartResourceServerAsync(Api);

        var response = await CallAsync(rs, "/api/items", "Bearer", body.GetProperty("access_token").GetString()!);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("items");
    }

    [Fact]
    public async Task An_access_token_without_the_scope_is_forbidden()
    {
        var (_, body) = await IssueAsync();
        using var rs = await StartResourceServerAsync(Api);

        var response = await CallAsync(rs, "/api/items/write", "Bearer", body.GetProperty("access_token").GetString()!);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "authenticated, but the token was not granted the scope the endpoint requires");
    }

    [Fact]
    public async Task A_request_without_a_token_is_challenged()
    {
        await SeedApplicationAsync(ClientId("webapp"));
        using var rs = await StartResourceServerAsync(Api);

        var response = await rs.GetTestClient().GetAsync("/api/items");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_access_token_for_another_audience_is_refused()
    {
        var (_, body) = await IssueAsync();
        using var rs = await StartResourceServerAsync(ClientId("another-api"));

        var response = await CallAsync(rs, "/api/items", "Bearer", body.GetProperty("access_token").GetString()!);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The resource server's audience is the client id here, so the id token passes the audience check and
    /// only the <c>at+jwt</c> type requirement (RFC 9068 §4) stands between it and the endpoint.
    /// </summary>
    [Fact]
    public async Task An_id_token_is_not_accepted_as_an_access_token()
    {
        var (app, body) = await IssueAsync();
        using var rs = await StartResourceServerAsync(app.ClientId);

        var response = await CallAsync(rs, "/api/items", "Bearer", body.GetProperty("id_token").GetString()!);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an id token is a signed JWT from the same issuer, but it asserts a sign-in to the client, not access to an API");
    }

    // ---------- proof of possession ----------

    [Fact]
    public async Task A_DPoP_bound_token_sent_as_Bearer_is_refused()
    {
        using var key = new DpopTestKey();
        var (_, body) = await IssueAsync(key);
        using var rs = await StartResourceServerAsync(Api);

        var response = await CallAsync(rs, "/api/items", "Bearer", body.GetProperty("access_token").GetString()!);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "RFC 9449 §7.1: otherwise a stolen bound token works like any bearer token");
    }

    [Fact]
    public async Task A_DPoP_bound_token_with_a_valid_proof_is_accepted_once_per_proof()
    {
        using var key = new DpopTestKey();
        var (_, body) = await IssueAsync(key);
        var token = body.GetProperty("access_token").GetString()!;
        using var rs = await StartResourceServerAsync(Api);
        var proof = key.Proof("GET", ItemsUrl, accessToken: token);

        (await CallAsync(rs, "/api/items", "DPoP", token, proof)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CallAsync(rs, "/api/items", "DPoP", token, proof)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a replayed proof must not authorize a second request");

        (await CallAsync(rs, "/api/items", "DPoP", token, key.Proof("GET", ItemsUrl, accessToken: token)))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a fresh proof from the same key does");
    }

    [Fact]
    public async Task A_DPoP_bound_token_with_a_proof_from_another_key_is_refused()
    {
        using var key = new DpopTestKey();
        using var thief = new DpopTestKey();
        var (_, body) = await IssueAsync(key);
        var token = body.GetProperty("access_token").GetString()!;
        using var rs = await StartResourceServerAsync(Api);

        var response = await CallAsync(rs, "/api/items", "DPoP", token, thief.Proof("GET", ItemsUrl, accessToken: token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_DPoP_bound_token_without_a_proof_is_refused()
    {
        using var key = new DpopTestKey();
        var (_, body) = await IssueAsync(key);
        using var rs = await StartResourceServerAsync(Api);

        var response = await CallAsync(rs, "/api/items", "DPoP", body.GetProperty("access_token").GetString()!);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unbound_token_sent_with_the_DPoP_scheme_is_refused()
    {
        using var key = new DpopTestKey();
        var (_, body) = await IssueAsync();
        var token = body.GetProperty("access_token").GetString()!;
        using var rs = await StartResourceServerAsync(Api);

        var response = await CallAsync(rs, "/api/items", "DPoP", token, key.Proof("GET", ItemsUrl, accessToken: token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the client believes the token is bound; accepting it would hide that it is not");
    }

    // ---------- introspection ----------

    /// <summary>
    /// <see cref="SparkIntrospectionHandler"/> fetches discovery through a process-wide, authority-keyed
    /// <c>ConfigurationManager</c> built on a plain <c>HttpDocumentRetriever</c> (not the HttpClient factory), so the
    /// only seam for an in-process issuer is to pre-seed that dictionary. Every OIDC test host uses the same issuer,
    /// and the endpoints the discovery document names are derived from it, so seeding with this class's handler is
    /// harmless to any other host.
    /// </summary>
    private void PointIntrospectionDiscoveryAtTheProvider()
    {
        var field = typeof(SparkIntrospectionHandler).GetField("Discovery", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SparkIntrospectionHandler.Discovery is gone; update this seam.");
        var discovery = (Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>)field.GetValue(null)!;
        lock (discovery)
        {
            discovery[Issuer] = new ConfigurationManager<OpenIdConnectConfiguration>(
                Issuer + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(new HttpClient(IdpHandler)) { RequireHttps = true });
        }
    }

    [Fact]
    public async Task Introspection_accepts_an_active_token_and_refuses_it_once_revoked()
    {
        var (app, body) = await IssueAsync();
        var token = body.GetProperty("access_token").GetString()!;
        // The resource server's own client: introspection answers a client whose id is the token's audience.
        await SeedApplicationAsync(Api);
        PointIntrospectionDiscoveryAtTheProvider();

        using var rs = await StartResourceServerAsync(Api, o =>
        {
            o.UseIntrospection = true;
            o.IntrospectionClientId = Api;
            o.IntrospectionClientSecret = Secret;
            o.IntrospectionCacheDuration = TimeSpan.Zero; // so the revocation is seen on the very next call
        });

        (await CallAsync(rs, "/api/items", "Bearer", token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CallAsync(rs, "/api/items/write", "Bearer", token)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "scopes come from the introspection answer's scope member");

        var revoke = await Client.PostAsync("/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = app.ClientId, ["client_secret"] = Secret, ["token"] = token,
        }));
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        (await CallAsync(rs, "/api/items", "Bearer", token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the point of introspection: a revoked token stops working at once, not when it expires");
    }
}
