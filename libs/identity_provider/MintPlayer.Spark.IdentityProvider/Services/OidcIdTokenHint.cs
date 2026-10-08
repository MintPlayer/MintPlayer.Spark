using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Reads the client an <c>id_token_hint</c> was issued to (OIDC RP-Initiated Logout 1.0 §2), for
/// <c>/connect/logout</c>.
/// </summary>
/// <remarks>
/// <para>
/// The stock ASP.NET OpenIdConnect handler signs out with <c>id_token_hint</c> and
/// <c>post_logout_redirect_uri</c> but no <c>client_id</c>, so without this the provider could never
/// honour its redirect (#490 M6, S6 gap 1).
/// </para>
/// <para>
/// ⚠️ The hint only names the client whose registered URIs are then checked, so it must be a token
/// <em>this</em> provider signed: the signature (this provider's own key) and the issuer are
/// validated. An expired hint is accepted, as the specification allows — the relying party usually
/// signs out long after its id_token expired. A token carrying a <c>scope</c> claim is an access
/// token, not an id_token, and is refused.
/// </para>
/// </remarks>
internal static class OidcIdTokenHint
{
    /// <summary>The client id the hint was issued to, or <see langword="null"/> if it is not a valid id_token of ours.</summary>
    public static async Task<string?> ResolveClientIdAsync(OidcSigningKeyService keys, string idTokenHint, string issuer)
    {
        var handler = new JsonWebTokenHandler();
        var validation = await handler.ValidateTokenAsync(idTokenHint, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            // The audience is what is being read, not what is being checked.
            ValidateAudience = false,
            ValidateLifetime = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = keys.GetSigningKey(),
        });

        if (!validation.IsValid || validation.SecurityToken is not JsonWebToken jwt)
            return null;

        if (jwt.TryGetPayloadValue<object>("scope", out _))
            return null;

        // Our id_tokens carry exactly one audience: the client they were issued to.
        var audiences = jwt.Audiences.ToArray();
        return audiences.Length == 1 && !string.IsNullOrEmpty(audiences[0]) ? audiences[0] : null;
    }
}
