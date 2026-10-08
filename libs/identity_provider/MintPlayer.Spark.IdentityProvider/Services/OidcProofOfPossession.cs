using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>The outcome of checking a request's proof of possession.</summary>
/// <param name="Confirmation">The access token's <c>cnf</c> claim: <c>jkt</c> (DPoP) or <c>x5t#S256</c> (mTLS), or null for a bearer token.</param>
internal sealed record OidcPossession(IReadOnlyDictionary<string, object>? Confirmation, string? Error = null, string? ErrorDescription = null)
{
    public bool Succeeded => Error is null;

    /// <summary><c>"DPoP"</c> for a DPoP-bound token, else <c>"Bearer"</c>.</summary>
    public string TokenType => Confirmation?.ContainsKey("jkt") == true ? "DPoP" : "Bearer";
}

/// <summary>
/// Sender-constrained access tokens (<c>docs/identity_provider_platform_PRD.md</c> D8):
/// <list type="bullet">
/// <item><b>DPoP</b> (RFC 9449): a <c>DPoP</c> header carrying a JWT signed with the client's key, for
/// this method and URL, fresh and used once. The token is then bound to that key (<c>cnf.jkt</c>), and a
/// resource server accepts it only with a proof from the same key. A client registered with
/// <c>RequireDpop</c> must send one.</item>
/// <item><b>Certificate binding</b> (RFC 8705 §3): for a client registered with
/// <c>TlsClientCertificateBoundAccessTokens</c>, the token carries the SHA-256 thumbprint of the TLS client
/// certificate (<c>cnf.x5t#S256</c>).</item>
/// </list>
/// </summary>
internal sealed class OidcProofOfPossession(IDocumentStore store, OidcIssuer issuer)
{
    public const string DpopHeader = "DPoP";
    public static readonly string[] DpopAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256];
    private static readonly TimeSpan ProofLifetime = TimeSpan.FromMinutes(2);

    /// <summary>The token endpoint's binding for <paramref name="app"/>, from the request's DPoP proof and TLS certificate.</summary>
    public async Task<OidcPossession> BindAsync(HttpContext http, OidcApplication app, X509Certificate2? certificate, CancellationToken ct)
    {
        var confirmation = new Dictionary<string, object>();

        var proof = http.Request.Headers[DpopHeader].ToString();
        if (!string.IsNullOrEmpty(proof))
        {
            var (jkt, error) = await ValidateProofAsync(http, proof, accessToken: null, ct);
            if (error is not null)
                return new(null, "invalid_dpop_proof", error);
            confirmation["jkt"] = jkt!;
        }
        else if (app.RequireDpop)
        {
            return new(null, "invalid_dpop_proof", "This client must send a DPoP proof.");
        }

        if (app.TlsClientCertificateBoundAccessTokens)
        {
            if (certificate is null)
                return new(null, "invalid_request", "This client's tokens are bound to its TLS client certificate, and none was presented.");
            confirmation["x5t#S256"] = Thumbprint(certificate);
        }

        return new(confirmation.Count > 0 ? confirmation : null);
    }

    public static string Thumbprint(X509Certificate2 certificate) => Base64UrlEncoder.Encode(certificate.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>
    /// Validates a DPoP proof (RFC 9449 §4.3) for this request and returns the proof key's thumbprint:
    /// <c>typ: dpop+jwt</c>, an asymmetric algorithm, an embedded public <c>jwk</c> that verifies the
    /// signature, <c>htm</c>/<c>htu</c> matching this request, a recent <c>iat</c>, a <c>jti</c> used once,
    /// and, when presented with an access token, <c>ath</c> = its hash.
    /// </summary>
    public async Task<(string? Jkt, string? Error)> ValidateProofAsync(HttpContext http, string proof, string? accessToken, CancellationToken ct)
    {
        JsonWebToken jwt;
        try { jwt = new JsonWebToken(proof); }
        catch (ArgumentException) { return (null, "The DPoP proof is not a JWT."); }

        if (jwt.Typ != "dpop+jwt")
            return (null, "The DPoP proof's typ is not dpop+jwt.");
        if (!DpopAlgorithms.Contains(jwt.Alg))
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
        if (!string.Equals(htm, http.Request.Method, StringComparison.Ordinal))
            return (null, "The DPoP proof is for another method.");
        var expectedUrl = issuer.Resolve(http.Request) + http.Request.Path;
        if (!Uri.TryCreate(htu, UriKind.Absolute, out var htuUri)
            || !string.Equals(htuUri.GetLeftPart(UriPartial.Path).TrimEnd('/'), expectedUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return (null, "The DPoP proof is for another URL.");

        var iat = jwt.IssuedAt;
        if (iat == DateTime.MinValue || iat < DateTime.UtcNow - ProofLifetime || iat > DateTime.UtcNow.AddMinutes(1))
            return (null, "The DPoP proof is not fresh.");

        if (accessToken is not null)
        {
            var ath = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
            if (jwt.GetPayloadValue<string?>("ath") != ath)
                return (null, "The DPoP proof is not for this access token.");
        }

        var jkt = Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
        if (string.IsNullOrEmpty(jwt.Id) || !await OidcReplayCache.TryUseAsync(store, $"dpop:{jkt}:{jwt.Id}", iat + ProofLifetime, ct))
            return (null, "The DPoP proof was already used.");

        return (jkt, null);
    }
}
