using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MintPlayer.Spark.Authorization.ResourceServer;

/// <summary>
/// DPoP proof validation (RFC 9449 §4.3), shared by the identity provider's token endpoint and Spark resource
/// servers (<c>docs/identity_provider_platform_PRD.md</c> D8, I12), so both halves apply the same rules.
/// </summary>
public static class SparkDpopProof
{
    /// <summary>The request header carrying the proof, and the authorization scheme of a DPoP-bound token.</summary>
    public const string Header = "DPoP";

    /// <summary>The proof algorithms accepted: asymmetric only, so the proof shows possession of a private key.</summary>
    public static readonly string[] Algorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256];

    /// <summary>How long a proof stays acceptable after its <c>iat</c>, and so how long its <c>jti</c> must be remembered.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Validates <paramref name="proof"/> and returns its key's thumbprint: <c>typ: dpop+jwt</c>, an asymmetric
    /// algorithm, an embedded public <c>jwk</c> that verifies the signature, <c>htm</c>/<c>htu</c> matching this
    /// request, a recent <c>iat</c>, a <c>jti</c> used once, and, with an access token, <c>ath</c> = its hash.
    /// </summary>
    /// <param name="expectedUrl">The request's URL without query or fragment, as the client addressed it.</param>
    /// <param name="tryUseJti">Records a proof id until the given moment; false when it was already recorded.</param>
    public static async Task<(string? Jkt, string? Error)> ValidateAsync(
        string proof, string method, string expectedUrl, string? accessToken, Func<string, DateTime, Task<bool>> tryUseJti)
    {
        JsonWebToken jwt;
        try { jwt = new JsonWebToken(proof); }
        catch (ArgumentException) { return (null, "The DPoP proof is not a JWT."); }

        if (jwt.Typ != "dpop+jwt")
            return (null, "The DPoP proof's typ is not dpop+jwt.");
        if (!Algorithms.Contains(jwt.Alg))
            return (null, "The DPoP proof's algorithm is not supported.");
        if (!jwt.TryGetHeaderValue<JsonElement>("jwk", out var jwkElement) || jwkElement.ValueKind != JsonValueKind.Object)
            return (null, "The DPoP proof carries no jwk.");

        JsonWebKey jwk;
        try { jwk = new JsonWebKey(jwkElement.GetRawText()); }
        catch (ArgumentException) { return (null, "The DPoP proof's jwk is not valid."); }
        if (jwk.HasPrivateKey)
            return (null, "The DPoP proof's jwk contains a private key.");

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(proof, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            IssuerSigningKey = jwk,
            ValidTypes = ["dpop+jwt"],
            RequireSignedTokens = true,
        });
        if (!result.IsValid)
            return (null, "The DPoP proof's signature is not valid.");

        var htm = jwt.GetPayloadValue<string?>("htm");
        var htu = jwt.GetPayloadValue<string?>("htu");
        if (!string.Equals(htm, method, StringComparison.Ordinal))
            return (null, "The DPoP proof is for another method.");
        if (!Uri.TryCreate(htu, UriKind.Absolute, out var htuUri)
            || !string.Equals(htuUri.GetLeftPart(UriPartial.Path).TrimEnd('/'), expectedUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return (null, "The DPoP proof is for another URL.");

        var iat = jwt.IssuedAt;
        if (iat == DateTime.MinValue || iat < DateTime.UtcNow - Lifetime || iat > DateTime.UtcNow.AddMinutes(1))
            return (null, "The DPoP proof is not fresh.");

        if (accessToken is not null && jwt.GetPayloadValue<string?>("ath") != AccessTokenHash(accessToken))
            return (null, "The DPoP proof is not for this access token.");

        var jkt = Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
        if (string.IsNullOrEmpty(jwt.Id) || !await tryUseJti($"dpop:{jkt}:{jwt.Id}", iat + Lifetime))
            return (null, "The DPoP proof was already used.");

        return (jkt, null);
    }

    /// <summary>The <c>ath</c> claim: base64url SHA-256 of the access token's ASCII bytes.</summary>
    public static string AccessTokenHash(string accessToken)
        => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));

    /// <summary>The <c>x5t#S256</c> confirmation of a certificate-bound token (RFC 8705 §3.1).</summary>
    public static string CertificateThumbprint(X509Certificate2 certificate)
        => Base64UrlEncoder.Encode(certificate.GetCertHash(HashAlgorithmName.SHA256));
}
