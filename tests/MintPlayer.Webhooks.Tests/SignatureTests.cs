using System.Net;
using System.Text;
using MintPlayer.Webhooks;
using Official = StandardWebhooks;

namespace MintPlayer.Webhooks.Tests;

/// <summary>
/// v1 signing and verification, cross-checked in both directions against the official Standard Webhooks library
/// (docs/apps_webhooks_PRD.md D20), and v2a against the spec's own test vector (D21).
/// </summary>
public class SignatureTests
{
    private const string Payload = """{"type":"contact.created","timestamp":"2022-11-03T20:26:10.344522Z","data":{"id":"1f81eb52-5198-4599-803e-771906343485"}}""";
    private const string MessageId = "msg_2KWPBgLlAfxdpx2AI54pPJ85f4W";
    private const long Timestamp = 1674087231;

    private static WebhookVerifier Verifier(Action<WebhookVerifierOptions> configure)
    {
        var options = new WebhookVerifierOptions { Clock = () => DateTimeOffset.FromUnixTimeSeconds(Timestamp) };
        configure(options);
        return new WebhookVerifier(options);
    }

    private static Func<string, string?> Headers(string id, long ts, string signature)
        => name => name switch
        {
            WebhookHeaders.Id => id,
            WebhookHeaders.Timestamp => ts.ToString(),
            WebhookHeaders.Signature => signature,
            _ => null,
        };

    [Fact]
    public void Official_library_signature_is_accepted()
    {
        var secret = WebhookSecret.Generate();
        var signature = new Official.Webhook(secret.ToString()).Sign(MessageId, DateTimeOffset.FromUnixTimeSeconds(Timestamp), Payload);

        var result = Verifier(o => o.Secrets.Add(secret.ToString())).Verify(Headers(MessageId, Timestamp, signature), Encoding.UTF8.GetBytes(Payload));

        result.IsValid.Should().BeTrue("a v1 signature made by the reference implementation must verify here");
        result.MessageId.Should().Be(MessageId, "the id is the receiver's idempotency key");
    }

    [Fact]
    public void Our_signature_is_accepted_by_the_official_library()
    {
        var secret = WebhookSecret.Generate();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var input = new WebhookSigningInput(MessageId, now, Encoding.UTF8.GetBytes(Payload));
        var header = WebhookSignatureHeader.Create([new HmacWebhookSigner(secret)], input);

        var headers = new WebHeaderCollection
        {
            { WebhookHeaders.Id, MessageId },
            { WebhookHeaders.Timestamp, now.ToString() },
            { WebhookHeaders.Signature, header },
        };

        var verify = () => new Official.Webhook(secret.ToString()).Verify(Payload, headers);
        verify.Should().NotThrow("the reference implementation must accept what MintPlayer.Spark.Apps sends");
    }

    [Fact]
    public void Tampered_body_is_rejected()
    {
        var secret = WebhookSecret.Generate();
        var signature = new Official.Webhook(secret.ToString()).Sign(MessageId, DateTimeOffset.FromUnixTimeSeconds(Timestamp), Payload);

        var result = Verifier(o => o.Secrets.Add(secret.ToString())).Verify(Headers(MessageId, Timestamp, signature), Encoding.UTF8.GetBytes(Payload.Replace("contact", "context")));

        result.Failure.Should().Be(WebhookVerificationFailure.InvalidSignature, "the signature covers every body byte");
    }

    [Fact]
    public void Timestamp_outside_tolerance_is_rejected_even_with_a_valid_signature()
    {
        var secret = WebhookSecret.Generate();
        var old = Timestamp - 301;
        var signature = new Official.Webhook(secret.ToString()).Sign(MessageId, DateTimeOffset.FromUnixTimeSeconds(old), Payload);

        var result = Verifier(o => o.Secrets.Add(secret.ToString())).Verify(Headers(MessageId, old, signature), Encoding.UTF8.GetBytes(Payload));

        result.Failure.Should().Be(WebhookVerificationFailure.TimestampOutOfTolerance, "a captured request must not be replayable after 5 minutes");
    }

    [Fact]
    public void Missing_headers_and_garbage_timestamp_are_rejected()
    {
        var verifier = Verifier(o => o.Secrets.Add(WebhookSecret.Generate().ToString()));
        verifier.Verify(_ => null, []).Failure.Should().Be(WebhookVerificationFailure.MissingHeaders, "nothing to verify");
        verifier.Verify(n => n == WebhookHeaders.Timestamp ? "-12" : "x", []).Failure
            .Should().Be(WebhookVerificationFailure.InvalidTimestamp, "a timestamp is unsigned unix seconds");
    }

    [Fact]
    public void Rotation_accepts_either_secret_and_a_header_with_both_signatures()
    {
        var oldSecret = WebhookSecret.Generate();
        var newSecret = WebhookSecret.Generate();
        var input = new WebhookSigningInput(MessageId, Timestamp, Encoding.UTF8.GetBytes(Payload));
        var bothSigned = WebhookSignatureHeader.Create([new HmacWebhookSigner(newSecret), new HmacWebhookSigner(oldSecret)], input);

        Verifier(o => o.Secrets.Add(oldSecret.ToString())).Verify(Headers(MessageId, Timestamp, bothSigned), input.Payload).IsValid
            .Should().BeTrue("a receiver that still has only the old secret must keep working during rotation");
        Verifier(o => o.Secrets.Add(newSecret.ToString())).Verify(Headers(MessageId, Timestamp, bothSigned), input.Payload).IsValid
            .Should().BeTrue("a receiver that switched early must work too");
        Verifier(o => o.Secrets.Add(WebhookSecret.Generate().ToString())).Verify(Headers(MessageId, Timestamp, bothSigned), input.Payload).IsValid
            .Should().BeFalse("an unrelated secret must not verify");
    }

    [Fact]
    public void Malformed_signature_entries_are_skipped_not_fatal()
    {
        var secret = WebhookSecret.Generate();
        var input = new WebhookSigningInput(MessageId, Timestamp, Encoding.UTF8.GetBytes(Payload));
        var header = "v9,zzz v1,not-base64!! v1 " + WebhookSignatureHeader.Create([new HmacWebhookSigner(secret)], input);

        Verifier(o => o.Secrets.Add(secret.ToString())).Verify(Headers(MessageId, Timestamp, header), input.Payload).IsValid
            .Should().BeTrue("a valid entry after unknown or broken ones still verifies");
    }

    [Fact]
    public void A_receiver_without_secrets_fails_closed()
    {
        var create = () => new WebhookVerifier(new WebhookVerifierOptions());
        create.Should().Throw<InvalidOperationException>("an unconfigured receiver must never accept everything");
    }

    [Fact]
    public void Secret_format_is_validated()
    {
        WebhookSecret.Generate().ToString().Should().StartWith("whsec_", "the prefix identifies a secret in config files");
        var tooShort = () => WebhookSecret.Parse("whsec_" + Convert.ToBase64String(new byte[23]));
        tooShort.Should().Throw<FormatException>("the spec sets 24 bytes as the minimum");
        var notBase64 = () => WebhookSecret.Parse("whsec_***");
        notBase64.Should().Throw<FormatException>("the key is base64");
        var unprefixed = WebhookSecret.Parse(Convert.ToBase64String(new byte[32]));
        unprefixed.GetKey().Length.Should().Be(32, "some producers omit the prefix");
    }

    // The spec's v2a example (spec/standard-webhooks.md, "Asymmetric signatures").
    private const string SpecSecretKey = "whsk_4uKPjji/dhB8w6GW3YYDnIMYvXcdHrvH6lidTr4Z+EY=";
    private const string SpecPublicKey = "whpk_uWKK13PfwqyOgrqT0OrWKcS2uVD7e89+oUKnkoPjx7w=";
    private const string SpecEndpointId = "ep_2fVZdC8Kx9TR4ZoCqHWt3NnLgRb";
    private const string SpecV2aSignature = "v2a,kG9XaxZjAEpKB8KeQQb5G1Xp73YH5Rf5gxoWe7cg1kYMiyspdtZXe9w+dAwKeOurAwxfqmlOG/QR4m9s37DzAg==";

    [Fact]
    public void V2a_matches_the_spec_test_vector()
    {
        var key = WebhookSigningKey.Parse(SpecSecretKey);
        key.PublicKey.ToString().Should().Be(SpecPublicKey, "the public key is derived from the whsk_ seed");

        var input = new WebhookSigningInput(MessageId, Timestamp, Encoding.UTF8.GetBytes(Payload), SpecEndpointId);
        WebhookSignatureHeader.Create([new Ed25519WebhookSigner(key)], input)
            .Should().Be(SpecV2aSignature, "ed25519 is deterministic, so the spec's signature must be reproduced byte for byte");
    }

    [Fact]
    public void V2a_spec_signature_verifies_only_for_its_endpoint()
    {
        var body = Encoding.UTF8.GetBytes(Payload);
        Verifier(o => o.AddEd25519PublicKey(SpecPublicKey, SpecEndpointId)).Verify(Headers(MessageId, Timestamp, SpecV2aSignature), body).IsValid
            .Should().BeTrue("the spec's v2a signature must verify with its public key");
        Verifier(o => o.AddEd25519PublicKey(SpecPublicKey, "ep_other")).Verify(Headers(MessageId, Timestamp, SpecV2aSignature), body).IsValid
            .Should().BeFalse("v2a binds the endpoint id, so a signature for one endpoint cannot be replayed to another");
    }

    [Fact]
    public void V2a_signing_requires_an_endpoint_id()
    {
        var sign = () => new Ed25519WebhookSigner(WebhookSigningKey.Generate()).Sign(new WebhookSigningInput(MessageId, Timestamp, []));
        sign.Should().Throw<InvalidOperationException>("v2a content starts with the endpoint id");
    }
}
