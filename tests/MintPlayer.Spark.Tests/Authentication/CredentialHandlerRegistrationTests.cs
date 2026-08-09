using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Replication.Authentication;

namespace MintPlayer.Spark.Tests.Authentication;

/// <summary>
/// M10 — the three credential handlers, and the refusals that keep them from being configured into
/// something that authenticates the wrong caller.
/// <para>
/// Each of these guards exists because the unsafe configuration is silent. A JWT scheme with no
/// audience accepts every token the issuer ever minted; certificate forwarding with no trusted
/// proxy turns a request header into a module identity. Neither produces an error at runtime — they
/// produce an application that authenticates attackers. So the refusal is at startup, where it
/// cannot be missed.
/// </para>
/// </summary>
public class CredentialHandlerRegistrationTests
{
    private static SparkBuilder NewBuilder() => new(new ServiceCollection());

    // --- M10.3, JWT bearer resource server -----------------------------------------------

    /// <summary>
    /// The confused-deputy guard. Without an audience the signature still verifies for a token a
    /// client obtained for a *different* resource, so the application would accept it as authority
    /// over this one.
    /// </summary>
    [Fact]
    public void A_JWT_credential_without_an_audience_is_refused_at_startup()
    {
        var builder = NewBuilder();

        var act = () => builder.AddJwtBearerCredential(o => o.Authority = "https://idp.test");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Audience*");
    }

    [Fact]
    public void A_JWT_credential_without_an_authority_is_refused_at_startup()
    {
        var builder = NewBuilder();

        var act = () => builder.AddJwtBearerCredential(o => o.Audience = "spark-api");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Authority*");
    }

    [Fact]
    public void A_configured_JWT_credential_joins_the_composite_as_non_ambient()
    {
        var builder = NewBuilder();

        builder.AddJwtBearerCredential(o =>
        {
            o.Authority = "https://idp.test";
            o.Audience = "spark-api";
        });

        builder.Registry.CredentialSchemes.Should().Contain(
            s => s.Name == SparkJwtBearerExtensions.Scheme && !s.IsAmbient,
            "a bearer token cannot be replayed by a cross-site page, so demanding an antiforgery "
            + "token of this caller would block the external POSTs it exists to enable");
    }

    // --- M10.2, certificate forwarding ---------------------------------------------------

    /// <summary>
    /// A forwarded certificate is an ordinary request header. Accepting it from anywhere means any
    /// caller that can reach the app directly can claim to be any module by setting a header —
    /// which is strictly worse than having no mTLS, because it looks like mTLS.
    /// </summary>
    [Fact]
    public void Certificate_forwarding_without_a_trusted_proxy_is_refused_at_startup()
    {
        var builder = NewBuilder();

        var act = () => builder.AddModuleCertificateForwarding(_ => { });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*KnownProxies*");
    }

    [Fact]
    public void Certificate_forwarding_defaults_to_the_ARR_header_and_accepts_an_override()
    {
        var options = new SparkCertificateForwardingOptions();
        options.HeaderName.Should().Be("X-ARR-ClientCert");

        var builder = NewBuilder();
        builder.AddModuleCertificateForwarding(o =>
        {
            // Traefik's header — the proxy this repository's own compose file uses.
            o.HeaderName = "X-Forwarded-Tls-Client-Cert";
            o.KnownProxies.Add(IPAddress.Parse("10.0.0.7"));
        });

        // Assert the header actually took effect, not merely that some service was registered —
        // the configured name is the whole point, since no two proxies agree on it.
        var configured = builder.Services.BuildServiceProvider()
            .GetRequiredService<IOptions<CertificateForwardingOptions>>().Value;

        configured.CertificateHeader.Should().Be("X-Forwarded-Tls-Client-Cert");
    }

    // --- M10.1, module certificate -------------------------------------------------------

    [Fact]
    public void The_module_certificate_scheme_joins_the_composite_as_non_ambient()
    {
        var builder = NewBuilder();

        builder.AddModuleCertificateAuthentication();

        builder.Registry.CredentialSchemes.Should().Contain(
            s => s.Name == SparkModuleCertificateDefaults.Scheme && !s.IsAmbient,
            "a browser cannot be induced to complete a TLS handshake with a module's private key");
    }

    /// <summary>
    /// The group claim is the entire authorization integration: a module becomes governable by
    /// <c>security.json</c> without the authorization model learning what a module is. The prefix
    /// is therefore part of the contract an operator writes against, not an internal detail.
    /// </summary>
    [Fact]
    public void A_module_resolves_to_a_predictable_security_json_group_name()
    {
        SparkModuleCertificateDefaults.GroupPrefix.Should().Be("Module:");
        (SparkModuleCertificateDefaults.GroupPrefix + "HR").Should().Be("Module:HR");
    }
}
