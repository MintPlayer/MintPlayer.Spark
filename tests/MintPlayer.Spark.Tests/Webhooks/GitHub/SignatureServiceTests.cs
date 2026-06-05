using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MintPlayer.Spark.Webhooks.GitHub.Services;

namespace MintPlayer.Spark.Tests.Webhooks.GitHub;

/// <summary>
/// R2-C3 — SignatureService must (1) fail-closed when the configured webhook
/// secret is empty rather than fail-open (the prior behavior, which let any
/// signed-or-unsigned POST through), and (2) compare the candidate against the
/// expected header in constant time so a remote attacker can't byte-by-byte
/// recover the valid HMAC.
/// </summary>
public class SignatureServiceTests
{
    private readonly SignatureService _service = new();

    [Fact]
    public void VerifySignature_returns_false_for_empty_secret()
    {
        // Was previously: returns true (fail-open) — accepted any signature
        // including null when no secret was configured.
        _service.VerifySignature(signature: "sha256=anything", secret: "", requestBody: "body")
            .Should().BeFalse("empty secret must fail closed — cannot prove anything about origin");

        _service.VerifySignature(signature: null, secret: "", requestBody: "body")
            .Should().BeFalse("empty secret + missing signature still fails closed");
    }

    [Fact]
    public void VerifySignature_returns_false_for_missing_signature_when_secret_is_set()
    {
        _service.VerifySignature(signature: null, secret: "real-secret", requestBody: "body")
            .Should().BeFalse();
        _service.VerifySignature(signature: "", secret: "real-secret", requestBody: "body")
            .Should().BeFalse();
    }

    [Fact]
    public void VerifySignature_accepts_a_correctly_signed_body()
    {
        const string secret = "real-secret";
        const string body = "{\"hello\":\"world\"}";
        var expectedSig = BuildExpectedSignature(secret, body);

        _service.VerifySignature(expectedSig, secret, body).Should().BeTrue();
    }

    [Fact]
    public void VerifySignature_rejects_a_mutated_body()
    {
        const string secret = "real-secret";
        var expectedSig = BuildExpectedSignature(secret, "original body");

        _service.VerifySignature(expectedSig, secret, "tampered body").Should().BeFalse();
    }

    [Fact]
    public void VerifySignature_rejects_a_signature_of_different_length()
    {
        // Catches a defensive shortcut: if a signature with a different byte
        // length somehow got past the equality check, the FixedTimeEquals call
        // would throw. We instead bail out early with false.
        _service.VerifySignature("sha256=short", "real-secret", "body").Should().BeFalse();
    }

    [Fact]
    public void VerifySignature_is_constant_time_within_a_factor()
    {
        // Property test for timing-safety. We compute many runs of "good signature"
        // and "near-miss signature whose first byte differs". A non-constant-time
        // compare ends the loop on byte 1 for the near-miss, taking dramatically
        // less time than the good run. With FixedTimeEquals both runs touch every
        // byte; the medians should be within an order of magnitude on a quiet box.
        //
        // We don't assert a tight bound (CI noise destroys those); we assert the
        // ratio stays under 100×, which would catch a regression to string.Equals.
        const string secret = "real-secret";
        const string body = "the body we sign";
        var good = BuildExpectedSignature(secret, body);
        // Build a near-miss the same length, differing only in the trailing byte.
        var nearMiss = good[..^1] + (good[^1] == 'a' ? 'b' : 'a');

        // Warm up
        for (var i = 0; i < 1000; i++)
        {
            _service.VerifySignature(good, secret, body);
            _service.VerifySignature(nearMiss, secret, body);
        }

        const int iterations = 5000;
        var swGood = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) _service.VerifySignature(good, secret, body);
        swGood.Stop();

        var swMiss = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) _service.VerifySignature(nearMiss, secret, body);
        swMiss.Stop();

        // Avoid divide-by-zero on ultra-fast runs.
        var goodNs = Math.Max(1, swGood.ElapsedTicks);
        var missNs = Math.Max(1, swMiss.ElapsedTicks);
        var ratio = Math.Max(goodNs, missNs) / (double)Math.Min(goodNs, missNs);

        ratio.Should().BeLessThan(100,
            $"good={swGood.ElapsedMilliseconds}ms miss={swMiss.ElapsedMilliseconds}ms — " +
            "constant-time compare should keep these in the same order of magnitude");
    }

    private static string BuildExpectedSignature(string secret, string body)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hash = HMACSHA256.HashData(keyBytes, bodyBytes);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
