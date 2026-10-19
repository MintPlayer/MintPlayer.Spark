using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using MintPlayer.Spark.Webhooks;

namespace MintPlayer.Spark.Tests.Webhooks;

/// <summary>
/// The three verification strategies of <c>MintPlayer.Spark.Webhooks</c>: an HMAC of the body in a
/// header, Stripe's timestamped scheme with a replay tolerance, and fetch-by-id. Every one verifies
/// the exact bytes it is given; a change of one byte must fail.
/// </summary>
public class WebhookSignatureStrategyTests
{
    private const string Secret = "whsec_test_secret";

    /// <summary>Bytes a JSON round trip would change: a trailing zero, an escaped slash, an offset.</summary>
    private static readonly byte[] Body = Encoding.UTF8.GetBytes(
        """{"id":"evt_1","amount":1.50,"url":"a\/b","created":"2026-10-19T08:45:12.000+02:00"}""");

    private static WebhookRequest Request(byte[] body, params (string Name, string Value)[] headers)
        => new(headers.Select(h => new KeyValuePair<string, StringValues>(h.Name, h.Value)), body);

    private static string HexHmac(string secret, byte[] data)
        => Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data));

    // ---- HMAC ---------------------------------------------------------------------------------

    private static readonly HmacSignatureScheme GitHubLike = new("X-Signature", WebhookHmacAlgorithm.Sha256, WebhookSignatureEncoding.Hex, "sha256=");

    [Fact]
    public void Hmac_accepts_a_correctly_signed_body()
    {
        var request = Request(Body, ("X-Signature", "sha256=" + HexHmac(Secret, Body)));

        var result = new HmacSignatureStrategy(GitHubLike, Secret).Verify(request);

        result.IsAccepted.Should().BeTrue();
        result.PayloadVerified.Should().BeTrue();
    }

    [Fact]
    public void Hmac_reads_the_header_case_insensitively()
    {
        var request = Request(Body, ("x-signature", "sha256=" + HexHmac(Secret, Body)));

        new HmacSignatureStrategy(GitHubLike, Secret).Verify(request).IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void Hmac_rejects_a_tampered_body()
    {
        var signature = "sha256=" + HexHmac(Secret, Body);
        // The change a JSON round trip makes: 1.50 → 1.5.
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body).Replace("1.50", "1.5"));

        var result = new HmacSignatureStrategy(GitHubLike, Secret).Verify(Request(tampered, ("X-Signature", signature)));

        result.IsAccepted.Should().BeFalse();
        result.Rejection.Should().Be(WebhookRejectionReason.InvalidSignature);
    }

    [Fact]
    public void Hmac_rejects_a_signature_made_with_another_secret()
    {
        var request = Request(Body, ("X-Signature", "sha256=" + HexHmac("another-secret", Body)));

        new HmacSignatureStrategy(GitHubLike, Secret).Verify(request).Rejection
            .Should().Be(WebhookRejectionReason.InvalidSignature);
    }

    [Fact]
    public void Hmac_rejects_a_missing_prefix()
    {
        var request = Request(Body, ("X-Signature", HexHmac(Secret, Body)));

        new HmacSignatureStrategy(GitHubLike, Secret).Verify(request).IsAccepted.Should().BeFalse();
    }

    [Fact]
    public void Hmac_rejects_a_missing_header()
    {
        new HmacSignatureStrategy(GitHubLike, Secret).Verify(Request(Body)).Rejection
            .Should().Be(WebhookRejectionReason.MissingSignature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Hmac_fails_closed_without_a_secret(string? secret)
    {
        // Even a header that is the HMAC under the empty key must not pass.
        var request = Request(Body, ("X-Signature", "sha256=" + HexHmac(string.Empty, Body)));

        new HmacSignatureStrategy(GitHubLike, secret).Verify(request).Rejection
            .Should().Be(WebhookRejectionReason.NoSecretConfigured);
    }

    [Fact]
    public void Hmac_supports_base64_and_other_algorithms()
    {
        var scheme = new HmacSignatureScheme("X-Shop-Hmac", WebhookHmacAlgorithm.Sha512, WebhookSignatureEncoding.Base64);
        var signature = Convert.ToBase64String(HMACSHA512.HashData(Encoding.UTF8.GetBytes(Secret), Body));

        new HmacSignatureStrategy(scheme, Secret).Verify(Request(Body, ("X-Shop-Hmac", signature))).IsAccepted.Should().BeTrue();
        new HmacSignatureStrategy(scheme with { Algorithm = WebhookHmacAlgorithm.Sha256 }, Secret)
            .Verify(Request(Body, ("X-Shop-Hmac", signature))).IsAccepted.Should().BeFalse("the algorithm is part of the scheme");
    }

    // ---- Stripe -------------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static StripeSignatureStrategy Stripe(string? secret = Secret, TimeSpan? tolerance = null)
        => new(secret, tolerance, new FixedTimeProvider(Now));

    private static WebhookRequest StripeRequest(string header, byte[]? body = null)
        => Request(body ?? Body, ("Stripe-Signature", header));

    [Fact]
    public void Stripe_accepts_a_fresh_correctly_signed_delivery()
    {
        var header = StripeSignatureStrategy.ComputeHeader(Secret, Now.AddSeconds(-30), Body);

        Stripe().Verify(StripeRequest(header)).IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void Stripe_signs_timestamp_dot_body_as_documented()
    {
        // Independent of ComputeHeader: HMAC-SHA256 over "{t}.{body}", lower-case hex, as Stripe documents.
        var t = Now.ToUnixTimeSeconds().ToString();
        var signed = Encoding.UTF8.GetBytes(t + ".").Concat(Body).ToArray();
        var header = $"t={t},v1={HexHmac(Secret, signed)}";

        Stripe().Verify(StripeRequest(header)).IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void Stripe_rejects_a_tampered_body()
    {
        var header = StripeSignatureStrategy.ComputeHeader(Secret, Now, Body);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Body).Replace("a\\/b", "a/b"));

        Stripe().Verify(StripeRequest(header, tampered)).Rejection.Should().Be(WebhookRejectionReason.InvalidSignature);
    }

    [Fact]
    public void Stripe_rejects_a_tampered_timestamp()
    {
        // Moving t forward to dodge the tolerance breaks the signature: t is part of what is signed.
        var header = StripeSignatureStrategy.ComputeHeader(Secret, Now.AddHours(-1), Body);
        var forged = "t=" + Now.ToUnixTimeSeconds() + header[header.IndexOf(',')..];

        Stripe().Verify(StripeRequest(forged)).Rejection.Should().Be(WebhookRejectionReason.InvalidSignature);
    }

    [Fact]
    public void Stripe_rejects_a_genuine_delivery_replayed_outside_the_tolerance()
    {
        var header = StripeSignatureStrategy.ComputeHeader(Secret, Now - StripeSignatureStrategy.DefaultTolerance - TimeSpan.FromSeconds(1), Body);

        var result = Stripe().Verify(StripeRequest(header));

        result.IsAccepted.Should().BeFalse();
        result.Rejection.Should().Be(WebhookRejectionReason.OutsideTolerance);
    }

    [Fact]
    public void Stripe_accepts_a_delivery_at_the_edge_of_the_tolerance()
    {
        var header = StripeSignatureStrategy.ComputeHeader(Secret, Now - StripeSignatureStrategy.DefaultTolerance, Body);

        Stripe().Verify(StripeRequest(header)).IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void Stripe_rejects_a_timestamp_too_far_in_the_future()
    {
        var header = StripeSignatureStrategy.ComputeHeader(Secret, Now.AddMinutes(10), Body);

        Stripe().Verify(StripeRequest(header)).Rejection.Should().Be(WebhookRejectionReason.OutsideTolerance);
    }

    [Fact]
    public void Stripe_honours_a_configured_tolerance()
    {
        var header = StripeSignatureStrategy.ComputeHeader(Secret, Now.AddMinutes(-2), Body);

        Stripe(tolerance: TimeSpan.FromMinutes(1)).Verify(StripeRequest(header)).Rejection
            .Should().Be(WebhookRejectionReason.OutsideTolerance);
        Stripe(tolerance: TimeSpan.FromMinutes(3)).Verify(StripeRequest(header)).IsAccepted.Should().BeTrue();
    }

    [Fact]
    public void Stripe_accepts_any_matching_v1_while_a_secret_is_rolled()
    {
        var genuine = StripeSignatureStrategy.ComputeHeader(Secret, Now, Body);
        var stale = StripeSignatureStrategy.ComputeHeader("whsec_old", Now, Body);
        var both = $"{stale},v1={genuine[(genuine.IndexOf("v1=") + 3)..]},v0=ignored";

        Stripe().Verify(StripeRequest(both)).IsAccepted.Should().BeTrue();
    }

    [Theory]
    [InlineData("v1=abc")]
    [InlineData("t=1760875200")]
    [InlineData("t=not-a-number,v1=abc")]
    [InlineData("garbage")]
    public void Stripe_rejects_a_malformed_header(string header)
    {
        Stripe().Verify(StripeRequest(header)).Rejection.Should().Be(WebhookRejectionReason.MalformedSignature);
    }

    [Fact]
    public void Stripe_fails_closed_without_a_secret()
    {
        var header = StripeSignatureStrategy.ComputeHeader(string.Empty, Now, Body);

        Stripe(secret: "").Verify(StripeRequest(header)).Rejection.Should().Be(WebhookRejectionReason.NoSecretConfigured);
    }

    // ---- Fetch by id --------------------------------------------------------------------------

    [Fact]
    public void FetchById_reads_the_id_from_a_form_body_and_marks_the_payload_unverified()
    {
        var request = Request(Encoding.UTF8.GetBytes("id=tr_WDqYK6vllg"), ("Content-Type", "application/x-www-form-urlencoded"));

        var result = new FetchByIdStrategy().Verify(request);

        result.IsAccepted.Should().BeTrue();
        result.PayloadVerified.Should().BeFalse("nothing is signed: the handler must fetch the resource");
        result.ResourceId.Should().Be("tr_WDqYK6vllg");
    }

    [Fact]
    public void FetchById_reads_the_id_from_a_json_body()
    {
        var request = Request(Encoding.UTF8.GetBytes("""{"id":"tr_abc","status":"paid"}"""), ("Content-Type", "application/json; charset=utf-8"));

        new FetchByIdStrategy().Verify(request).ResourceId.Should().Be("tr_abc");
    }

    [Theory]
    [InlineData("status=paid")]
    [InlineData("id=")]
    [InlineData("id=..%2F..%2Fadmin")]
    [InlineData("id=a&id=b")]
    public void FetchById_rejects_a_missing_or_unsafe_id(string body)
    {
        var request = Request(Encoding.UTF8.GetBytes(body), ("Content-Type", "application/x-www-form-urlencoded"));

        new FetchByIdStrategy().Verify(request).Rejection.Should().Be(WebhookRejectionReason.MissingResourceId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
