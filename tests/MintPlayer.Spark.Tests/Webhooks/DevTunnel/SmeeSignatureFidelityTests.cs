using MintPlayer.Spark.Webhooks.GitHub.DevTunnel.Services;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace MintPlayer.Spark.Tests.Webhooks.DevTunnel;

/// <summary>
/// The end-to-end statement of the bug: a body relayed through the tunnel must still verify
/// against the signature GitHub computed over the original bytes.
/// <para>
/// This is the assertion the previous implementation could not pass. It went through
/// <c>Smee.IO.Client</c>, which materialized the body as a Newtonsoft <c>JObject</c>, and every
/// delivery carrying a timestamp failed here and was silently dropped.
/// </para>
/// </summary>
public class SmeeSignatureFidelityTests
{
    private const string Secret = "it's-a-secret-to-everybody";

    // An installation payload in GitHub's own shape: fractional-second timestamp with a UTC offset
    // that is deliberately NOT the CI machine's, plus a trailing-zero decimal.
    private const string GitHubBody =
        """{"action":"created","installation":{"id":153131096,"created_at":"2026-08-12T08:45:12.000+02:00","account":{"login":"MintPlayer"}},"ratio":1.50}""";

    private static string Sign(string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static string FrameFor(string body, string signature)
        => $$"""{"host":"https://smee.io","x-github-event":"installation","x-hub-signature-256":"{{signature}}","body":{{body}},"timestamp":1700000000}""";

    [Fact]
    public void A_relayed_delivery_still_verifies_against_GitHubs_signature()
    {
        // GitHub signs the bytes it sends, then smee relays them inside its envelope.
        var signature = Sign(GitHubBody);

        SmeeBodyReader.TryReadDelivery(FrameFor(GitHubBody, signature), out var headers, out var body)
            .Should().BeTrue();

        new SignatureService()
            .VerifySignature(headers!["x-hub-signature-256"].ToString(), Secret, body!)
            .Should().BeTrue();
    }

    [Fact]
    public void The_verification_does_not_depend_on_the_host_timezone()
    {
        // Structural, not incidental: the reader never parses the payload into values, so there is
        // no date handling for TimeZoneInfo.Local to influence. The offset in the extracted text is
        // the offset GitHub sent, on any machine.
        SmeeBodyReader.TryReadDelivery(FrameFor(GitHubBody, Sign(GitHubBody)), out _, out var body)
            .Should().BeTrue();

        body!.Contains("2026-08-12T08:45:12.000+02:00").Should().BeTrue();
    }

    [Fact]
    public void A_body_altered_in_transit_fails_verification()
    {
        // Guards the guard: if the fidelity test above ever passes vacuously, this one fails.
        var signature = Sign(GitHubBody);
        var tampered = GitHubBody.Replace("\"ratio\":1.50", "\"ratio\":1.5");

        SmeeBodyReader.TryReadDelivery(FrameFor(tampered, signature), out var headers, out var body)
            .Should().BeTrue();

        new SignatureService()
            .VerifySignature(headers!["x-hub-signature-256"].ToString(), Secret, body!)
            .Should().BeFalse();
    }
}
