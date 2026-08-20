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
public class OidcLocalCredentialModeTests : SparkTestDriver
{
    private SparkEndpointFactory<OidcTestContext> CreateFactory(SparkLocalCredentials mode) =>
        new(
            Store,
            models: [],
            configureSpark: spark =>
            {
                spark.AddAuthentication<SparkUser>(
                    configure: auth => auth.LocalCredentials = mode,
                    // Disabled mode requires an external provider — an application nobody can sign
                    // into is rejected at startup, which is itself covered by LocalCredentialModeTests.
                    configureProviders: identity => identity.Services
                        .AddAuthentication()
                        .AddCookie("GitHub", "GitHub", _ => { }));
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
    public async Task Disabled_mode_removes_the_identity_provider_login_pages()
    {
        await using var factory = CreateFactory(SparkLocalCredentials.Disabled);

        var routes = RoutesOf(factory);

        routes.Should().NotContain("/connect/login");
        routes.Should().NotContain("/connect/two-factor");
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
