using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// Where the issuer comes from. Every endpoint test pins <c>Issuer</c>, so the two fallbacks —
/// the Development-only Host-header derivation and the refusal everywhere else — never ran.
/// </summary>
public class OidcIssuerTests
{
    private static string Resolve(string? issuer, string environment)
    {
        var oidcIssuer = new OidcIssuer(
            new SparkIdentityProviderOptions { Issuer = issuer },
            new HostingEnvironment { EnvironmentName = environment });

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("idp.example.test:8443");
        return oidcIssuer.Resolve(context.Request);
    }

    [Fact]
    public void A_configured_issuer_wins_and_loses_its_trailing_slash()
        => Resolve("https://idp.test/", Environments.Production).Should().Be("https://idp.test");

    [Fact]
    public void Development_derives_the_issuer_from_the_request()
        => Resolve(null, Environments.Development).Should().Be("https://idp.example.test:8443");

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void An_unset_issuer_outside_development_is_refused(string? issuer)
    {
        var act = () => Resolve(issuer, Environments.Production);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Issuer must be set outside Development*");
    }
}
