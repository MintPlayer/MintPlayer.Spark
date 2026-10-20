using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Challenges.Contracts;

namespace MintPlayer.Spark.Challenges.Altcha;

/// <summary>
/// ALTCHA, built in (research §6.4): a self-hosted proof of work in the altcha-lib v1 protocol (SHA-256), with no third
/// party, so it needs no consent and is the fallback for a visitor who refuses a hosted provider's (GA9).
/// </summary>
/// <remarks>
/// <para>
/// The protocol (altcha-lib v1, <c>createChallenge</c>/<c>verifySolution</c>): the server picks a random
/// <c>number ≤ maxnumber</c> and a salt carrying <c>?expires=</c>, publishes <c>challenge = hex(SHA-256(salt + number))</c>
/// and a <c>signature</c> over it; the client searches for the number and sends back base64 JSON
/// <c>{algorithm, challenge, number, salt, signature}</c>. The server recomputes the hash and checks the signature.
/// </para>
/// <para>
/// altcha-lib's signature is HMAC-SHA256 under a configured key. Here the signature is Spark's Data Protection seal over
/// the challenge and the point (research §6.10: "the HMAC key comes from Data Protection, never from configuration"). The
/// widget treats the signature as opaque, so the wire format is unchanged; a challenge offered for one point cannot be
/// spent at another. The used-challenge marker (replay) is kept by <see cref="ISparkHumanChallenges"/>.
/// </para>
/// <para>
/// Interop with the <c>altcha</c> npm widget was not run in this milestone (no npm runs); the ng-spark client solves the
/// same protocol with WebCrypto (K11).
/// </para>
/// </remarks>
public sealed class AltchaChallenge(IDataProtectionProvider dataProtection, IOptionsMonitor<SparkChallengesOptions> options, TimeProvider time) : ISparkHumanChallenge
{
    public const string ProviderName = "altcha";
    internal const string ProtectorPurpose = "MintPlayer.Spark.Challenges.Altcha.v1";
    private const string Algorithm = "SHA-256";

    public string Name => ProviderName;

    public bool RequiresConsent => false;

    private IDataProtector Protector => dataProtection.CreateProtector(ProtectorPurpose);

    public Task<SparkChallengeOffer> OfferAsync(SparkChallengeRequest request, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue.Altcha;
        var maxNumber = Math.Max(1_000, settings.MaxNumber);
        var expires = time.GetUtcNow() + (settings.Expires > TimeSpan.Zero ? settings.Expires : TimeSpan.FromMinutes(10));
        var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)) + "?expires=" + expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var number = RandomNumberGenerator.GetInt32(0, (int)Math.Min(maxNumber, int.MaxValue - 1) + 1);
        var challenge = Hash(salt, number);
        var signature = Protector.Protect(Seal(request.Point, challenge));
        return Task.FromResult(new SparkChallengeOffer(
            ProviderName, request.Point, Altcha: new SparkAltchaChallenge(Algorithm, challenge, maxNumber, salt, signature)));
    }

    public Task<SparkChallengeResult> VerifyAsync(SparkChallengeRequest request, string response, CancellationToken cancellationToken)
        => Task.FromResult(Verify(request.Point, response));

    public string ReplayKey(string response)
        => TryDecode(response, out var payload) ? "altcha:" + payload!.Challenge : SparkChallengeHashing.Sha256Hex(response);

    internal SparkChallengeResult Verify(string point, string response)
    {
        if (!TryDecode(response, out var payload) || payload is null
            || !string.Equals(payload.Algorithm, Algorithm, StringComparison.OrdinalIgnoreCase)
            || payload.Challenge is not { Length: 64 } || payload.Salt is null || payload.Signature is null || payload.Number < 0)
            return SparkChallengeResult.Fail(ProviderName, "invalid");

        if (!TryReadExpiry(payload.Salt, out var expires) || time.GetUtcNow() > expires)
            return SparkChallengeResult.Fail(ProviderName, "expired");

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Hash(payload.Salt, payload.Number)), Encoding.ASCII.GetBytes(payload.Challenge.ToLowerInvariant())))
            return SparkChallengeResult.Fail(ProviderName, "invalid");

        string sealedValue;
        try
        {
            sealedValue = Protector.Unprotect(payload.Signature);
        }
        catch (CryptographicException)
        {
            return SparkChallengeResult.Fail(ProviderName, "invalid");
        }
        return string.Equals(sealedValue, Seal(point, payload.Challenge.ToLowerInvariant()), StringComparison.Ordinal)
            ? SparkChallengeResult.Pass(ProviderName)
            : SparkChallengeResult.Fail(ProviderName, "invalid");
    }

    /// <summary><c>hex(SHA-256(salt + number))</c>, the altcha-lib v1 challenge.</summary>
    public static string Hash(string salt, long number)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + number.ToString(CultureInfo.InvariantCulture))));

    private static string Seal(string point, string challenge) => point + "|" + challenge;

    internal static bool TryReadExpiry(string salt, out DateTimeOffset expires)
    {
        expires = default;
        var query = salt.IndexOf('?');
        if (query < 0)
            return false;
        foreach (var part in salt[(query + 1)..].Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == "expires" && long.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
                return true;
            }
        }
        return false;
    }

    internal static bool TryDecode(string response, out AltchaPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(response) || response.Length > 4096)
            return false;
        try
        {
            var json = Convert.FromBase64String(response.Trim());
            payload = JsonSerializer.Deserialize<AltchaPayload>(json);
            return payload is not null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The solution the client sends (base64 JSON).</summary>
    internal sealed record AltchaPayload(
        [property: JsonPropertyName("algorithm")] string? Algorithm,
        [property: JsonPropertyName("challenge")] string? Challenge,
        [property: JsonPropertyName("number")] long Number,
        [property: JsonPropertyName("salt")] string? Salt,
        [property: JsonPropertyName("signature")] string? Signature);
}
