using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Authorization.ResourceServer;
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
    public const string DpopHeader = SparkDpopProof.Header;
    public static readonly string[] DpopAlgorithms = SparkDpopProof.Algorithms;

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

    public static string Thumbprint(X509Certificate2 certificate) => SparkDpopProof.CertificateThumbprint(certificate);

    /// <summary>
    /// Validates a DPoP proof (RFC 9449 §4.3, <see cref="SparkDpopProof"/>) for this request, against the issuer's own URL, and returns the proof key's thumbprint:
    /// <c>typ: dpop+jwt</c>, an asymmetric algorithm, an embedded public <c>jwk</c> that verifies the
    /// signature, <c>htm</c>/<c>htu</c> matching this request, a recent <c>iat</c>, a <c>jti</c> used once,
    /// and, when presented with an access token, <c>ath</c> = its hash.
    /// </summary>
    public Task<(string? Jkt, string? Error)> ValidateProofAsync(HttpContext http, string proof, string? accessToken, CancellationToken ct)
        => SparkDpopProof.ValidateAsync(proof, http.Request.Method, issuer.Resolve(http.Request) + http.Request.Path, accessToken,
            (key, expiresAt) => OidcReplayCache.TryUseAsync(store, key, expiresAt, ct));
}
