using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MintPlayer.Spark.IdentityProvider.Models;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Encrypts an id_token or a signed userinfo response for a client that registered
/// <c>*_encrypted_response_alg</c> (OIDC Core §10.2, <c>docs/identity_provider_platform_PRD.md</c> D8):
/// sign first, then encrypt the signed JWT to the client's key (a nested JWT, <c>cty: JWT</c>).
/// </summary>
/// <remarks>
/// Spike S2: Wilson encrypts content with the <c>A*CBC-HS*</c> algorithms only, so those are what
/// discovery advertises; <c>A256GCM</c> is refused at registration rather than failing at issuance.
/// </remarks>
internal sealed class OidcJwe(OidcClientKeys clientKeys)
{
    public static readonly string[] KeyAlgorithms = [SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.RsaOaepKeyWrap];
    public static readonly string[] ContentAlgorithms = [SecurityAlgorithms.Aes128CbcHmacSha256, SecurityAlgorithms.Aes256CbcHmacSha512];

    /// <summary>
    /// <paramref name="signedJwt"/> encrypted as <paramref name="algorithm"/>/<paramref name="encryption"/>, or
    /// unchanged when no encryption is registered. Null when encryption is registered and the client has
    /// no usable key: a token the client asked to receive encrypted is never sent in the clear.
    /// </summary>
    public async Task<string?> EncryptAsync(string signedJwt, OidcApplication app, string? algorithm, string? encryption, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(algorithm))
            return signedJwt;

        var key = await clientKeys.GetEncryptionKeyAsync(app, algorithm, ct);
        if (key is null)
            return null;

        var credentials = new EncryptingCredentials(key, algorithm, string.IsNullOrEmpty(encryption) ? SecurityAlgorithms.Aes128CbcHmacSha256 : encryption);
        return new JsonWebTokenHandler().EncryptToken(signedJwt, credentials);
    }
}
