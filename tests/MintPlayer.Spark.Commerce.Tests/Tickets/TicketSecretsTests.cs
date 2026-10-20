using System.Text;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;

namespace MintPlayer.Spark.Commerce.Tests.Tickets;

/// <summary>
/// The ticket secret (§4.11, default 9, S8): 128 random bits, PBKDF2-HMAC-SHA256 verifier, QR payload <c>ST1.{code}.{secret}</c>.
/// The same vector is checked by the scanner's WebCrypto twin (<c>ng-spark/ticketing/tickets/src/ticket-secrets.spec.ts</c>),
/// so server and offline device hash alike.
/// </summary>
public class TicketSecretsTests
{
    [Fact]
    public void Pbkdf2_matches_the_published_HMAC_SHA256_vector()
    {
        // RFC 7914 §11 / the widely published PBKDF2-HMAC-SHA256 vector: P = "password", S = "salt", c = 1, dkLen = 32.
        var derived = TicketSecrets.Derive("password", Encoding.UTF8.GetBytes("salt"), 1);
        Convert.ToHexStringLower(derived).Should().Be("120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b");
    }

    [Fact]
    public void A_secret_verifies_against_its_own_verifier_only()
    {
        var secret = TicketSecrets.NewSecret();
        var verifier = TicketSecrets.Hash(secret, 1_000);

        TicketSecrets.Verify(verifier, secret).Should().BeTrue();
        TicketSecrets.Verify(verifier, TicketSecrets.NewSecret()).Should().BeFalse();
        TicketSecrets.Verify(verifier, null).Should().BeFalse();
        TicketSecrets.Verify(verifier, "").Should().BeFalse();
        TicketSecrets.Verify(verifier with { Iterations = 999 }, secret).Should().BeFalse();
        // A fresh salt per hash: the same secret never gives the same verifier twice.
        TicketSecrets.Hash(secret, 1_000).Hash.Should().NotBe(verifier.Hash);
    }

    [Fact]
    public void Secrets_carry_128_bits_and_codes_80()
    {
        var secret = TicketSecrets.NewSecret();
        secret.Length.Should().Be(22);
        TicketSecrets.TryFromBase64Url(secret, out var bytes).Should().BeTrue();
        bytes.Length.Should().Be(16);

        var code = TicketSecrets.NewCode();
        TicketSecrets.IsCode(code).Should().BeTrue();
        code.Length.Should().Be(16);
        Enumerable.Range(0, 200).Select(_ => TicketSecrets.NewCode()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_QR_payload_parses_with_or_without_its_prefix()
    {
        var code = TicketSecrets.NewCode();
        var secret = TicketSecrets.NewSecret();
        var payload = TicketSecrets.Payload(code, secret);
        payload.Should().StartWith("ST1.");

        TicketSecrets.TryParse(payload, out var c1, out var s1).Should().BeTrue();
        c1.Should().Be(code);
        s1.Should().Be(secret);

        TicketSecrets.TryParse("  " + code.ToLowerInvariant() + "." + secret + " ", out var c2, out var s2).Should().BeTrue();
        c2.Should().Be(code);
        s2.Should().Be(secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ST1.")]
    [InlineData("ST1.ABC.def")]
    [InlineData("ST1.0123456789ABCDEF.")]
    [InlineData("ST1.0123456789ABCDEF.short")]
    [InlineData("ST1.0123456789ABCDEU.aaaaaaaaaaaaaaaaaaaaaa")] // U is not Crockford base 32
    [InlineData("ST1.0123456789ABCDEF.aaaaaaaaaaaaaaaaaaaaa+")] // not base64url
    public void Anything_else_is_not_a_payload(string? payload)
        => TicketSecrets.TryParse(payload, out _, out _).Should().BeFalse();

    [Fact]
    public void Device_tokens_are_compared_by_hash()
    {
        var token = TicketSecrets.NewToken();
        var hash = TicketSecrets.TokenHash(token);
        TicketSecrets.TokenMatches(token, hash).Should().BeTrue();
        TicketSecrets.TokenMatches(TicketSecrets.NewToken(), hash).Should().BeFalse();
        TicketSecrets.TokenMatches(null, hash).Should().BeFalse();
        TicketSecrets.TokenMatches(token, null).Should().BeFalse();
    }

    [Fact]
    public void The_vault_returns_the_secret_of_the_current_generation_only()
    {
        var vault = new TicketSecretVault(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var code = TicketSecrets.NewCode();
        var stored = vault.Protect(code, "s3cret-value-1234567", 2);

        vault.Unprotect(stored, code, 2).Should().Be("s3cret-value-1234567");
        vault.Unprotect(stored, code, 3).Should().BeNull();
        vault.Unprotect(stored, TicketSecrets.NewCode(), 2).Should().BeNull();
        vault.Unprotect(new TicketSecret { Protected = "garbage", Generation = 2 }, code, 2).Should().BeNull();
        vault.Unprotect(null, code, 2).Should().BeNull();
    }
}
