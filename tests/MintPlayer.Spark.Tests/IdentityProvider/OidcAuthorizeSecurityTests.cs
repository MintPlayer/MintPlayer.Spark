using System.Net;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// <c>/connect/authorize</c> — the checks that run before any user is involved, so they are
/// observable without a login. Case ids refer to docs/idp-e2e-test-matrix.md §A.
/// </summary>
public class OidcAuthorizeSecurityTests : OidcTestHost
{
    private static string Url(
        string clientId = "webapp",
        string? redirectUri = "https://webapp.test/cb",
        string responseType = "code",
        string scope = "openid",
        string? extra = null)
        => $"/connect/authorize?client_id={Uri.EscapeDataString(clientId)}"
         + (redirectUri is null ? "" : $"&redirect_uri={Uri.EscapeDataString(redirectUri)}")
         + $"&response_type={Uri.EscapeDataString(responseType)}"
         + $"&scope={Uri.EscapeDataString(scope)}"
         + (extra ?? "");

    /// <summary>A-C4 — the case that turns the O25 fix from reasoning into an observation.</summary>
    [Fact]
    public async Task Authorize_client_id_lookup_is_case_sensitive()
    {
        await SeedApplicationAsync("AcmeApp", redirectUris: ["https://acme.test/cb"]);

        var response = await Client.GetAsync(Url("acmeapp", "https://acme.test/cb"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "RavenDB matches strings case-insensitively by default; if the lookup does not opt "
            + "out, 'acmeapp' resolves the application registered as 'AcmeApp' — impersonation "
            + "by casing, on the lookup that decides which client every later check applies to");
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_client");
    }

    /// <summary>A-C4, positive half — the exact id must still resolve.</summary>
    [Fact]
    public async Task Authorize_accepts_the_exactly_registered_client_id()
    {
        await SeedApplicationAsync("AcmeApp", redirectUris: ["https://acme.test/cb"]);

        var response = await Client.GetAsync(Url("AcmeApp", "https://acme.test/cb"));

        // Unauthenticated, so the client checks passed and it fell through to the login hop.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith("/connect/login");
    }

    /// <summary>A-C1.</summary>
    [Fact]
    public async Task Authorize_rejects_unknown_client()
    {
        var response = await Client.GetAsync(Url("nobody-registered-this"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_client");
    }

    /// <summary>A-C2.</summary>
    [Fact]
    public async Task Authorize_rejects_disabled_application()
    {
        await SeedApplicationAsync("disabled-app", redirectUris: ["https://disabled.test/cb"], enabled: false);

        var response = await Client.GetAsync(Url("disabled-app", "https://disabled.test/cb"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_client");
    }

    /// <summary>A-C3 — regression pin for O11.</summary>
    [Fact]
    public async Task Authorize_rejects_a_client_credentials_only_client()
    {
        var seeded = await SeedApplicationAsync(
            "machine-only",
            redirectUris: ["https://machine.test/cb"],
            grantTypes: ["client_credentials"]);

        // Guard the fixture itself: if the seed silently kept the model's default of
        // ["authorization_code"], the assertion below would pass for the wrong reason.
        using (var session = Store.OpenAsyncSession())
        {
            var stored = await session.LoadAsync<OidcApplication>(seeded.Id!);
            stored.AllowedGrantTypes.Should().ContainSingle().Which.Should().Be("client_credentials");
        }

        var response = await Client.GetAsync(Url("machine-only", "https://machine.test/cb"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unauthorized_client",
            "a machine client typically holds broader application claims than any user, so it "
            + "must not be drivable through the interactive flow");
    }

    /// <summary>A-R1 — the destination must be one the client registered.</summary>
    [Fact]
    public async Task Authorize_rejects_an_unregistered_redirect_uri()
    {
        await SeedApplicationAsync("webapp");

        var response = await Client.GetAsync(Url(redirectUri: "https://evil.example.com/cb"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unvalidated redirect_uri must never become a redirect — the rejection has to be "
            + "a direct error, not a bounce to the attacker's URL");
        response.Headers.Location.Should().BeNull();
    }

    /// <summary>A-R2 — exact match, so a registered prefix is not enough.</summary>
    [Theory]
    [InlineData("https://webapp.test/cb2")]
    [InlineData("https://webapp.test/cb/extra")]
    [InlineData("https://webapp.test/cb/")]
    [InlineData("https://WebApp.test/cb")]
    public async Task Authorize_rejects_near_miss_redirect_uris(string redirectUri)
    {
        await SeedApplicationAsync("webapp");

        var response = await Client.GetAsync(Url(redirectUri: redirectUri));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A-T1/A-T2 — implicit and hybrid must be unreachable.</summary>
    [Theory]
    [InlineData("token")]
    [InlineData("id_token")]
    [InlineData("Code")]
    public async Task Authorize_rejects_unsupported_response_types(string responseType)
    {
        await SeedApplicationAsync("webapp");

        var response = await Client.GetAsync(Url(responseType: responseType));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unsupported_response_type");
    }

    /// <summary>A-S1 — a scope outside the client's allowed set.</summary>
    [Fact]
    public async Task Authorize_rejects_a_scope_the_client_may_not_hold()
    {
        await SeedApplicationAsync("webapp", allowedScopes: ["openid"]);

        var response = await Client.GetAsync(Url(scope: "openid admin"));

        // redirect_uri is validated by this point, so an error redirect is the correct shape.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("invalid_scope");
    }

    /// <summary>
    /// A-U4 / O16 — a bearer credential must not satisfy "a user is signed in". Presenting a
    /// bearer token here should be no better than presenting nothing: the request still gets
    /// sent to the login page rather than minting anything.
    /// </summary>
    [Fact]
    public async Task Authorize_does_not_accept_a_bearer_token_as_an_interactive_session()
    {
        await SeedApplicationAsync("webapp");

        using var client = Client;
        client.DefaultRequestHeaders.Add("Authorization", "Bearer not-an-interactive-session");

        var response = await client.GetAsync(Url());

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith("/connect/login",
            "the authorization-code grant delegates a person's authority, so only the cookie the "
            + "login page issues may satisfy it — ambient resolution picks the bearer scheme first");
    }

    /// <summary>A-U1 — and nothing is written for an unauthenticated caller.</summary>
    [Fact]
    public async Task Authorize_writes_no_request_document_before_the_user_signs_in()
    {
        await SeedApplicationAsync("webapp");

        await Client.GetAsync(Url());

        using var session = Store.OpenAsyncSession();
        var requests = await session.Query<OidcAuthorizationRequest>().ToListAsync();

        requests.Should().BeEmpty("the request is only persisted once there is a subject to bind it to");
    }
}
