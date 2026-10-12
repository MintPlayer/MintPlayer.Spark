using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Extensions;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Pins that the identity provider's own password form honours the application's
/// <see cref="SparkLocalCredentials"/> mode.
/// </summary>
/// <remarks>
/// Without this, an application could report that local credentials are disabled while still
/// serving a password form on <c>/connect/login</c> — which is worse than having no flag at all,
/// because the flag would be believed.
/// </remarks>
public class OidcLocalCredentialModeTests(SparkSharedDatabase database)
    : SparkSharedTestDriver(database), IClassFixture<SparkSharedDatabase>
{
    private SparkEndpointFactory<OidcTestContext> CreateFactory(SparkLocalCredentials mode) =>
        new(
            Store,
            models: [],
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>(
                    configure: auth => { auth.LocalCredentials = mode; auth.AllowUnconfirmedRegistration = true; });
                // Disabled mode requires an external provider — an application nobody can sign
                // into is rejected at startup, which is itself covered by LocalCredentialModeTests.
                spark.Services
                    .AddAuthentication()
                    .AddCookie("GitHub", "GitHub", _ => { });
                spark.AddIdentityProvider(options =>
                {
                    options.Issuer = "https://idp.test";
                    options.SigningKeyPath = Path.Combine(
                        Path.GetTempPath(), "spark-oidc-test-" + Guid.NewGuid().ToString("N") + ".json");
                });
            },
            environment: "Development");

    private static IReadOnlyList<string> RoutesOf(SparkEndpointFactory<OidcTestContext> factory) =>
        [.. factory.GetService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)];

    [Fact]
    public async Task The_provider_refuses_to_start_without_a_user_type()
    {
        // The token endpoint and the login submit are generic over the application's user type and
        // are closed when the routes are mapped. Without AddAuthentication<TUser>() there is nothing
        // to close them over; refusing at startup replaces a 500 on the first token request.
        // The factory starts its host when it is constructed, so construction is what throws.
        var start = async () =>
        {
            await using var factory = new SparkEndpointFactory<OidcTestContext>(
                Store,
                models: [],
                configureSpark: spark => spark.AddIdentityProvider(options =>
                {
                    options.Issuer = "https://idp.test";
                    options.SigningKeyPath = Path.Combine(
                        Path.GetTempPath(), "spark-oidc-test-" + Guid.NewGuid().ToString("N") + ".json");
                }),
                environment: "Development");
        };

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SparkModuleRegistry.IdentityUserType is null*");
    }

    [Fact]
    public async Task Disabled_mode_removes_the_password_surface_but_keeps_the_login_page()
    {
        // #490 M6 (D7): the GET stays mapped so a federating provider can offer its external
        // schemes; it used to 404. The POST and the two-factor step are the password surface.
        await using var factory = CreateFactory(SparkLocalCredentials.Disabled);

        var loginMethods = factory.GetService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText == "/connect/login")
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [])
            .ToArray();

        loginMethods.Should().Equal("GET");
        RoutesOf(factory).Should().NotContain("/connect/two-factor");
    }

    [Fact]
    public async Task Disabled_mode_login_page_shows_the_external_buttons_and_no_password_form()
    {
        await using var factory = CreateFactory(SparkLocalCredentials.Disabled);
        var returnUrl = "/connect/authorize?client_id=app&scope=openid profile&state=a&b";

        var response = await factory.CreateClient().GetAsync($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        var html = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).Should().Be(200);
        html.Should().NotContain("type=\"password\"");
        html.Should().NotContain("<form");
        html.Should().Contain("Sign in with GitHub");

        // The link, HTML-decoded, carries the pending authorization as returnUrl and this page as errorUrl.
        var href = System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Match(html, "href=\"([^\"]*external-login[^\"]*)\"").Groups[1].Value);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(href[href.IndexOf('?')..]);
        href.Should().StartWith("/spark/auth/external-login?");
        query["provider"].ToString().Should().Be("GitHub");
        query["returnUrl"].ToString().Should().Be(returnUrl);
        query["errorUrl"].ToString().Should().Be($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        // Raw markup: the URL's own separators are HTML-encoded inside the attribute.
        html.Should().Contain("&amp;returnUrl=");
        html.Should().NotContain("?provider=GitHub&returnUrl=");
    }

    [Fact]
    public async Task Full_mode_login_page_shows_the_form_and_the_external_buttons()
    {
        await using var factory = CreateFactory(SparkLocalCredentials.Full);

        var html = await (await factory.CreateClient().GetAsync("/connect/login?returnUrl=%2F")).Content.ReadAsStringAsync();

        html.Should().Contain("type=\"password\"");
        html.Should().Contain("Sign in with GitHub");
    }

    [Fact]
    public async Task Login_page_links_to_registration_with_the_local_return_url_only_when_registration_is_on()
    {
        // Owner request during the live MCP checks (2026-10-12): a new user who arrives from an agent's
        // authorization request needs a way to create an account and come back to it.
        var returnUrl = "/connect/authorize?client_id=https%3A%2F%2Fclaude.ai%2Fa&resource=https%3A%2F%2Fapp.test%2Fmcp&state=a&b";
        static string? RegisterHref(string html)
        {
            var match = System.Text.RegularExpressions.Regex.Match(html, "href=\"(/register[^\"]*)\"");
            return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
        }

        await using (var full = CreateFactory(SparkLocalCredentials.Full))
        {
            var client = full.CreateClient();
            var html = await (await client.GetAsync($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}")).Content.ReadAsStringAsync();

            // ng-spark's withRegistration() route, with the pending authorization as returnUrl.
            RegisterHref(html).Should().Be($"/register?returnUrl={Uri.EscapeDataString(returnUrl)}");
            html.Should().Contain("Create an account");

            // An off-site returnUrl is never carried: the provider only ever builds local ones.
            foreach (var offSite in new[] { "https://attacker.test/phish", "//evil.com", "/\\evil.com" })
            {
                var page = await (await client.GetAsync($"/connect/login?returnUrl={Uri.EscapeDataString(offSite)}")).Content.ReadAsStringAsync();
                RegisterHref(page).Should().BeNull(offSite);
            }
        }

        // SignInOnly: the password form stays, but nobody can register, so there is nothing to link to.
        await using var signInOnly = CreateFactory(SparkLocalCredentials.SignInOnly);
        var withoutRegistration = await (await signInOnly.CreateClient().GetAsync($"/connect/login?returnUrl={Uri.EscapeDataString(returnUrl)}")).Content.ReadAsStringAsync();
        withoutRegistration.Should().Contain("type=\"password\"");
        RegisterHref(withoutRegistration).Should().BeNull();
    }

    [Fact]
    public async Task Disabled_mode_keeps_the_oidc_protocol_endpoints()
    {
        // The discriminating half: an identity provider that federates to an upstream provider
        // still needs every protocol endpoint. If this went red the feature would have broken OIDC
        // rather than narrowed it.
        await using var factory = CreateFactory(SparkLocalCredentials.Disabled);

        var routes = RoutesOf(factory);

        routes.Should().Contain("/connect/authorize");
        routes.Should().Contain("/connect/token");
        routes.Should().Contain("/connect/userinfo");
        routes.Should().Contain("/connect/introspect");
        routes.Should().Contain("/connect/revoke");
        routes.Should().Contain("/connect/logout");
        routes.Should().Contain("/connect/consent");
        routes.Should().Contain("/.well-known/openid-configuration");
        routes.Should().Contain("/.well-known/jwks");
    }

    [Fact]
    public async Task Full_mode_keeps_the_identity_provider_login_pages()
    {
        await using var factory = CreateFactory(SparkLocalCredentials.Full);

        var routes = RoutesOf(factory);

        routes.Should().Contain("/connect/login");
        routes.Should().Contain("/connect/two-factor");
    }
}
