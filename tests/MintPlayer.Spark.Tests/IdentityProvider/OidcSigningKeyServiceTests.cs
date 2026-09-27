using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The signing key's life across restarts. Every other test lets the provider generate a fresh
/// key, so the load path — the one every production start takes — was never executed: "a token
/// issued before a restart still validates after it" was an assumption, not a tested property.
/// </summary>
public class OidcSigningKeyServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"spark-oidc-key-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private HostingEnvironment Env(string name)
        => new() { EnvironmentName = name, ContentRootPath = _directory };

    [Fact]
    public void A_generated_key_is_persisted_and_reloaded_after_a_restart()
    {
        // Relative, so it resolves against the content root as the default "App_Data/..." does.
        const string relativePath = "App_Data/oidc-signing-key.json";

        var first = new OidcSigningKeyService(Env(Environments.Development), relativePath);
        File.Exists(Path.Combine(_directory, relativePath)).Should().BeTrue();

        // Production on the second start: loading must not depend on the Development-only
        // generation path being available.
        var second = new OidcSigningKeyService(Env(Environments.Production), relativePath);

        second.GetPublicJwk().N.Should().Be(first.GetPublicJwk().N);
        second.GetPublicJwk().E.Should().Be(first.GetPublicJwk().E);
        second.GetSigningKey().KeyId.Should().Be(first.GetSigningKey().KeyId);

        // The property that matters: a token signed before the restart validates after it.
        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://idp.test",
            Audience = "webapp",
            SigningCredentials = new SigningCredentials(first.GetSigningKey(), SecurityAlgorithms.RsaSha256),
        });
        var result = handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "https://idp.test",
            ValidAudience = "webapp",
            IssuerSigningKey = second.GetSigningKey(),
        }).GetAwaiter().GetResult();

        result.IsValid.Should().BeTrue(result.Exception?.Message ?? "");
    }

    /// <summary>
    /// Each persisted component goes through the Base64Url decoder; a set of keys makes it very
    /// likely that some component lands on each padding length, which one key alone may not.
    /// </summary>
    [Fact]
    public void Reloading_is_exact_for_many_keys()
    {
        for (var i = 0; i < 8; i++)
        {
            var path = Path.Combine(_directory, $"key-{i}.json");
            var generated = new OidcSigningKeyService(Env(Environments.Development), path);
            var loaded = new OidcSigningKeyService(Env(Environments.Production), path);

            var expected = generated.GetSigningKey().Rsa.ExportParameters(true);
            var actual = loaded.GetSigningKey().Rsa.ExportParameters(true);
            actual.D.Should().Equal(expected.D);
            actual.P.Should().Equal(expected.P);
            actual.InverseQ.Should().Equal(expected.InverseQ);
        }
    }

    [Fact]
    public void A_missing_key_outside_development_refuses_to_start()
    {
        var path = Path.Combine(_directory, "absent.json");

        var act = () => new OidcSigningKeyService(Env(Environments.Production), path);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Auto-generation is only available in Development*");
        File.Exists(path).Should().BeFalse("a production host must never mint its own key silently");
    }
}
