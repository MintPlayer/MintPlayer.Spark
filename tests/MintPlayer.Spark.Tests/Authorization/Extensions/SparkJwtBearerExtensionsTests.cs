using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Builder;
using MintPlayer.Spark.Authorization.Extensions;

namespace MintPlayer.Spark.Tests.Authorization.Extensions;

/// <summary>
/// What <see cref="SparkJwtBearerExtensions.AddJwtBearerCredential"/> actually configures on the
/// JWT handler. The registration refusals are pinned in <c>CredentialHandlerRegistrationTests</c>;
/// these resolve the named options, which is the only way the configure lambda ever runs.
/// </summary>
public class SparkJwtBearerExtensionsTests
{
    private static JwtBearerOptions Configure(Action<SparkJwtBearerOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        new SparkBuilder(services).AddJwtBearerCredential(configure);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(SparkJwtBearerExtensions.Scheme);
    }

    [Fact]
    public void Authority_audience_and_metadata_policy_are_carried_to_the_handler()
    {
        var jwt = Configure(o =>
        {
            o.Authority = "https://idp.example.test/";
            o.Audience = "spark-api";
            o.RequireHttpsMetadata = false;
        });

        jwt.Authority.Should().Be("https://idp.example.test/");
        jwt.Audience.Should().Be("spark-api");
        jwt.RequireHttpsMetadata.Should().BeFalse();
    }

    [Fact]
    public void Https_metadata_is_required_unless_turned_off()
    {
        var jwt = Configure(o =>
        {
            o.Authority = "https://idp.example.test";
            o.Audience = "spark-api";
        });

        jwt.RequireHttpsMetadata.Should().BeTrue();
    }

    [Fact]
    public void Claim_types_are_kept_verbatim()
    {
        // A mapped "group" becomes a WS-Federation URI and every caller resolves to zero groups.
        var jwt = Configure(o =>
        {
            o.Authority = "https://idp.example.test";
            o.Audience = "spark-api";
        });

        jwt.MapInboundClaims.Should().BeFalse();
        jwt.TokenValidationParameters.NameClaimType.Should().Be("sub");
        jwt.TokenValidationParameters.RoleClaimType.Should().Be("role");
    }

    [Fact]
    public void Issuer_audience_lifetime_and_signature_are_all_validated()
    {
        var jwt = Configure(o =>
        {
            o.Authority = "https://idp.example.test/";
            o.Audience = "spark-api";
        });

        var parameters = jwt.TokenValidationParameters;
        parameters.ValidateIssuer.Should().BeTrue();
        parameters.ValidateAudience.Should().BeTrue();
        parameters.ValidateLifetime.Should().BeTrue();
        parameters.ValidateIssuerSigningKey.Should().BeTrue();
        parameters.ValidAudience.Should().Be("spark-api");
        parameters.ValidIssuer.Should().Be("https://idp.example.test",
            "an issuer claim carries no trailing slash, so a configured one must not make every token fail");
        parameters.ClockSkew.Should().Be(TimeSpan.FromMinutes(5));
        parameters.IssuerSigningKey.Should().BeNull("keys come from the authority's JWKS so they follow rotation");
    }
}
