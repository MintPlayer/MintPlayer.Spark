using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using PO = MintPlayer.Spark.Abstractions.PersistentObject;
using POA = MintPlayer.Spark.Abstractions.PersistentObjectAttribute;

using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// M12.7 through the route rather than the Actions class.
/// <para>
/// <see cref="OidcApplicationActionsTests"/> calls <c>OnBeforeSaveAsync</c> directly, which proves
/// the rules and nothing about whether anything reaches them. That gap is not hypothetical: writing
/// these tests is what showed that a refusal from an Actions class had no path to the caller at all
/// — it left the endpoint as an unhandled exception, so every message the audit was careful to
/// phrase for an operator arrived as a 500 with no body. An admin screen whose validation cannot
/// speak is not an admin screen.
/// </para>
/// <para>
/// These cases drive the model the package ships as a library layer (<c>identity-provider</c>) —
/// the host is given no fixture models at all — so a change to the entity or the shipped model that
/// breaks the screens fails here rather than in someone's deployment.
/// </para>
/// <para>
/// The client id and the secrets are read-only on the screen now (D5: generated server-side), so
/// the cases find their application by its display name, and a case that needs a secret attaches
/// one to the stored document, as the secret-issuing flow would.
/// </para>
/// <para>
/// One host per class (M8 item 11), booted by <see cref="AdminRouteHost"/>. The cases register
/// distinct display names and look their own up by them, so they can share it; the antiforgery
/// pair is minted once, as in <c>RetryFromEveryHook</c>.
/// </para>
/// </summary>
public class OidcAdminRouteTests(OidcAdminRouteTests.AdminRouteHost host)
    : SparkSharedTestDriver(host), IClassFixture<OidcAdminRouteTests.AdminRouteHost>
{
    /// <summary>Nothing of the package's: its persistent objects come from its library layer.</summary>
    public sealed class AdminContext : SparkContext
    {
    }

    public sealed class AdminRouteHost : SharedSparkHost<AdminContext>
    {
        public HttpClient Client { get; private set; } = null!;
        public string Cookies { get; private set; } = null!;
        public string Xsrf { get; private set; } = null!;

        protected override SparkEndpointFactory<AdminContext> CreateFactory() =>
            new(
                Store,
                models: [],
                configureSpark: spark =>
                {
                    // The provider closes its user-generic endpoints (/connect/token, the login
                    // submit) over the registered user type, and refuses to start without one.
                    spark.AddAuthentication<SparkUser>(
                        configure: auth => { auth.LocalCredentials = SparkLocalCredentials.Full; auth.AllowUnconfirmedRegistration = true; });

                    // The provider is here so the success cases can end where they should: not at
                    // "a document exists" but at "the protocol endpoint accepts what the screen wrote".
                    spark.AddIdentityProvider(options =>
                    {
                        options.Issuer = "https://idp.test";
                        options.SigningKeyPath = Path.Combine(
                            Path.GetTempPath(), "spark-oidc-admin-" + Guid.NewGuid().ToString("N") + ".json");
                    });
                },
                environment: "Development");

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            Client = Factory.CreateClient();
            (Cookies, Xsrf) = await Factory.MintAntiforgeryAsync();
        }

        public override async Task DisposeAsync()
        {
            Client?.Dispose();
            await base.DisposeAsync();
        }
    }

    /// <summary>The applications registered under <paramref name="displayName"/> (the client id is generated).</summary>
    private async Task<List<OidcApplication>> RegisteredAsync(string displayName)
    {
        await Store.WaitForIndexingAsync();
        using var session = Store.OpenAsyncSession();
        return await session.Query<OidcApplication>()
            .Where(a => a.DisplayName == displayName, exact: true)
            .ToListAsync();
    }

    private Task<HttpResponseMessage> PostAsync(
        string typeName, Dictionary<string, object?> attributes, bool withAntiforgery = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/spark/po/create")
        {
            Content = JsonContent.Create(Wire.Typed(typeName, new
            {
                PersistentObject = new PO
                {
                    Name = typeName,
                    ObjectTypeId = Guid.Empty,
                    Attributes = [.. attributes.Select(a => new POA { Name = a.Key, Value = a.Value })],
                },
            })),
        };

        request.Headers.Add("Cookie", host.Cookies);
        if (withAntiforgery)
            request.Headers.Add("X-XSRF-TOKEN", host.Xsrf);

        return host.Client.SendAsync(request);
    }

    /// <summary>The message an operator would read, pulled out of the standard errors envelope.</summary>
    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var errors = body.GetProperty("result").GetProperty("errors");

        return string.Join(" | ", errors.EnumerateArray().Select(e =>
        {
            var message = e.GetProperty("errorMessage");
            // TranslatedString serializes as a per-culture map, or as a bare string.
            return message.ValueKind == JsonValueKind.String
                ? message.GetString()
                : message.EnumerateObject().First().Value.GetString();
        }));
    }

    /// <summary>
    /// A complete application form. Every attribute is present because a rendered form posts every
    /// field — a test that sends only what it cares about is refused by the declarative validator
    /// for missing value types, which would make each case below pass or fail for the wrong reason.
    /// <para>
    /// The client id and secrets are posted as a screen would, and dropped by the model's read-only
    /// gate: the client id is generated on create (D5), so the case's name travels as the display name.
    /// </para>
    /// </summary>
    private static Dictionary<string, object?> Application(string name, params (string Name, object? Value)[] overrides)
    {
        var form = new Dictionary<string, object?>
        {
            ["ClientId"] = name,
            ["DisplayName"] = name,
            ["ClientType"] = "confidential",
            ["Enabled"] = true,
            ["Secrets"] = Array.Empty<object>(),
            ["AllowedGrantTypes"] = new[] { "authorization_code" },
            ["MayIntrospectAnyAudience"] = false,
            ["RedirectUris"] = new[] { $"https://{name}.test/cb" },
            ["PostLogoutRedirectUris"] = Array.Empty<string>(),
            ["AllowedCorsOrigins"] = Array.Empty<string>(),
            ["Scopes"] = new object[] { new { Name = "openid", Required = true, Status = OidcScopeStatuses.Approved } },
            ["Claims"] = Array.Empty<object>(),
            ["ConsentType"] = "explicit",
            ["AllowRememberConsent"] = true,
            ["RequirePkce"] = true,
            ["AccessTokenLifetimeMinutes"] = 60,
            ["RefreshTokenLifetimeDays"] = 14,
            ["AllowImpersonation"] = false,
            ["BackChannelLogoutSessionRequired"] = false,
            ["FrontChannelLogoutSessionRequired"] = false,
            ["RequireDpop"] = false,
            ["RequirePushedAuthorizationRequests"] = false,
            ["RequireSignedRequestObject"] = false,
            ["TlsClientCertificateBoundAccessTokens"] = false,
        };

        foreach (var (key, value) in overrides)
            form[key] = value;

        return form;
    }

    /// <summary>A complete API-resource form: <paramref name="name"/> is the audience, its scopes start with it.</summary>
    private static Dictionary<string, object?> ApiResource(string name, params string[] scopes)
    {
        return new Dictionary<string, object?>
        {
            ["Kind"] = OidcResourceKinds.Api,
            ["Name"] = name,
            ["ClaimTypes"] = Array.Empty<string>(),
            ["Owners"] = Array.Empty<string>(),
            ["Scopes"] = scopes.Select(s => (object)new { Name = s, Enabled = true, ShowInDiscoveryDocument = true }).ToArray(),
            ["Required"] = false,
            ["Emphasize"] = false,
            ["AutoApprove"] = false,
            ["AllowIntrospection"] = true,
            ["ShowInDiscoveryDocument"] = true,
            ["Enabled"] = true,
        };
    }

    [Fact]
    public async Task Registering_a_client_through_the_route_persists_it()
    {
        var response = await PostAsync("OidcApplication", Application("route-webapp"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var stored = (await RegisteredAsync("route-webapp")).Should().ContainSingle().Which;
        stored.RedirectUris.Should().ContainSingle().Which.Should().Be("https://route-webapp.test/cb");
        stored.ClientId.Should().NotBeNullOrEmpty().And.NotBe("route-webapp",
            "the client id is generated server-side; the screen cannot choose it");
    }

    [Fact]
    public async Task A_client_registered_through_the_route_can_obtain_a_token()
    {
        // The claim M12.7 rests on, end to end: an operator fills in the screens and the protocol
        // endpoint honours the result. Every step in between — the shipped model, the mapper, the
        // interceptors that generate the client id and give the resource its natural id — is
        // load-bearing and none of it is asserted directly.
        (await PostAsync("OidcResource", ApiResource("routeapi", "routeapi.read")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        (await PostAsync("OidcApplication", Application("route-service",
            ("RedirectUris", Array.Empty<string>()),
            ("Scopes", new object[] { new { Name = "routeapi.read", Required = false, Status = OidcScopeStatuses.Approved } }),
            ("AllowedGrantTypes", new[] { "client_credentials" }))))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        // Client lookup rides an eventually-consistent index, so a just-registered application is
        // not usable the instant the screen returns — the same trap the fixture hit when seeding.
        var app = (await RegisteredAsync("route-service")).Single();

        // Secrets are read-only on the screen; attach one the way the secret-issuing flow stores it.
        using (var session = Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<OidcApplication>(app.Id!);
            stored.Secrets.Add(new ClientSecret { SecretId = Guid.NewGuid().ToString("N"), Hash = ClientSecretHasher.Hash("issued-secret"), CreatedAt = DateTime.UtcNow });
            await session.SaveChangesAsync();
        }

        await Store.WaitForIndexingAsync();

        var token = await host.Client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = app.ClientId,
                ["client_secret"] = "issued-secret",
                ["scope"] = "routeapi.read",
            }));

        token.StatusCode.Should().Be(HttpStatusCode.OK,
            "a client and an API registered through the screens must be usable by the protocol endpoint");

        var payload = JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement;
        payload.GetProperty("access_token").GetString().Should().NotBeNullOrEmpty();
        payload.GetProperty("scope").GetString().Should().Be("routeapi.read");
    }

    [Fact]
    public async Task A_secret_typed_into_the_screen_is_never_stored()
    {
        // Was: the typed secret is stored hashed. Secrets are read-only on the screen now (issued by
        // the server, shown once), so the property that survives is the stronger one: a plaintext
        // posted with the form reaches the store in no form at all. Hashing itself is covered by
        // OidcApplicationActionsTests.
        (await PostAsync("OidcApplication", Application("route-hashing",
            ("Secrets", new object[] { new { Hash = "plaintext-from-the-form" } }))))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var app = (await RegisteredAsync("route-hashing")).Single();

        app.Secrets.Should().BeEmpty("the screen cannot write secrets");
    }

    [Fact]
    public async Task A_refused_registration_returns_the_reason_rather_than_a_500()
    {
        var response = await PostAsync("OidcApplication", Application("route-bad-uri",
            ("RedirectUris", new[] { "/callback" })));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorMessageAsync(response)).Should().Contain("absolute",
            "the operator is the only one who can fix this, so the reason has to reach the screen");
    }

    [Fact]
    public async Task An_unsupported_grant_type_is_refused_at_the_route()
    {
        var response = await PostAsync("OidcApplication", Application("route-implicit",
            ("AllowedGrantTypes", new[] { "implicit" })));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorMessageAsync(response)).Should().Contain("not supported");
    }

    [Fact]
    public async Task Two_registrations_posting_the_same_client_id_get_distinct_ones()
    {
        // Was: a duplicate client id is refused at the route. The screen can no longer choose the
        // client id (D5), so the duplicate cannot be posted; what must still hold is that two
        // applications never share one — that makes impersonation a matter of index ordering.
        (await PostAsync("OidcApplication", Application("route-twin")))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await PostAsync("OidcApplication", Application("route-twin")))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var twins = await RegisteredAsync("route-twin");

        twins.Should().HaveCount(2);
        twins.Select(a => a.ClientId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_resource_name_with_whitespace_is_refused_at_the_route()
    {
        var response = await PostAsync("OidcResource", ApiResource("api read"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorMessageAsync(response)).Should().Contain("space-delimited");
    }

    [Fact]
    public async Task Registration_requires_an_antiforgery_token()
    {
        // These screens decide who may mint tokens. A registration a cross-site POST can perform
        // is a client-registration endpoint open to any page the operator happens to visit.
        var response = await PostAsync("OidcApplication", Application("route-csrf"), withAntiforgery: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await RegisteredAsync("route-csrf")).Should().BeEmpty("a rejected request must not have written anything");
    }
}
