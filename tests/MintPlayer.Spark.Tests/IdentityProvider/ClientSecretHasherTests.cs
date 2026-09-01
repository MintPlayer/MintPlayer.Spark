using MintPlayer.Spark.IdentityProvider.Endpoints;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;

namespace MintPlayer.Spark.Tests.IdentityProvider;

public class ClientSecretHasherTests
{
    [Fact]
    public void Hash_never_contains_the_secret()
    {
        const string secret = "a-very-recognisable-secret-value";

        ClientSecretHasher.Hash(secret).Should().NotContain(secret);
    }

    [Fact]
    public void Hash_is_salted_so_the_same_secret_hashes_differently_each_time()
    {
        const string secret = "same-secret";

        var first = ClientSecretHasher.Hash(secret);
        var second = ClientSecretHasher.Hash(secret);

        first.Should().NotBe(second, "a per-hash random salt is what stops two clients sharing a secret being visibly identical");
        ClientSecretHasher.Verify(secret, first).Should().BeTrue();
        ClientSecretHasher.Verify(secret, second).Should().BeTrue();
    }

    [Fact]
    public void Verify_accepts_the_correct_secret_and_rejects_others()
    {
        var stored = ClientSecretHasher.Hash("correct-horse");

        ClientSecretHasher.Verify("correct-horse", stored).Should().BeTrue();
        ClientSecretHasher.Verify("correct-horse ", stored).Should().BeFalse();
        ClientSecretHasher.Verify("Correct-Horse", stored).Should().BeFalse();
        ClientSecretHasher.Verify("", stored).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2$sha256$100000$only-two-parts")]
    [InlineData("pbkdf2$sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2$sha256$0$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2$sha256$100000$!!!not-base64!!!$aGFzaA==")]
    public void Verify_rejects_malformed_stored_values_without_throwing(string stored)
    {
        // A corrupt record must not authenticate, and must not crash the token endpoint.
        ClientSecretHasher.Verify("any-secret", stored).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_the_legacy_unsalted_sha256_format()
    {
        // The ported implementation stored bare base64url SHA-256. Those values must no
        // longer authenticate rather than being silently accepted.
        var legacy = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("hr-dev-secret")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        ClientSecretHasher.Verify("hr-dev-secret", legacy).Should().BeFalse();
    }

    [Fact]
    public void Stored_format_records_its_own_parameters()
    {
        // Self-describing, so the work factor can be raised later without invalidating
        // secrets already stored under the old one.
        var stored = ClientSecretHasher.Hash("s");

        stored.Should().StartWith("pbkdf2$sha256$");
        stored.Split('$').Should().HaveCount(5);
    }

    [Fact]
    public void GenerateSecret_is_urlsafe_and_unpredictable()
    {
        var a = ClientSecretHasher.GenerateSecret();
        var b = ClientSecretHasher.GenerateSecret();

        a.Should().NotBe(b);
        a.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
        a.Length.Should().BeGreaterThan(40, "256 bits of entropy in base64url");
    }
}

public class VerifyClientSecretTests
{
    private static ClientSecret Stored(string secret, DateTime? expiresAt = null)
        => new() { Hash = ClientSecretHasher.Hash(secret), ExpiresAt = expiresAt };

    [Fact]
    public void No_secrets_configured_rejects()
        => Token.VerifyClientSecret("anything", []).Should().BeFalse();

    [Fact]
    public void Matching_unexpired_secret_is_accepted()
        => Token.VerifyClientSecret("s3cret", [Stored("s3cret")]).Should().BeTrue();

    [Fact]
    public void Wrong_secret_is_rejected()
        => Token.VerifyClientSecret("wrong", [Stored("s3cret")]).Should().BeFalse();

    [Fact]
    public void Expired_secret_is_rejected_even_though_it_matches()
    {
        var expired = Stored("s3cret", DateTime.UtcNow.AddMinutes(-1));

        Token.VerifyClientSecret("s3cret", [expired]).Should().BeFalse();
    }

    [Fact]
    public void Rotation_accepts_either_live_secret()
    {
        // The rotation window: old and new both valid until the old one expires.
        List<ClientSecret> secrets = [Stored("old", DateTime.UtcNow.AddDays(1)), Stored("new")];

        Token.VerifyClientSecret("old", secrets).Should().BeTrue();
        Token.VerifyClientSecret("new", secrets).Should().BeTrue();
        Token.VerifyClientSecret("other", secrets).Should().BeFalse();
    }

    [Fact]
    public void An_expired_secret_does_not_shadow_a_live_one()
    {
        List<ClientSecret> secrets = [Stored("retired", DateTime.UtcNow.AddMinutes(-1)), Stored("current")];

        Token.VerifyClientSecret("current", secrets).Should().BeTrue();
        Token.VerifyClientSecret("retired", secrets).Should().BeFalse();
    }
}
