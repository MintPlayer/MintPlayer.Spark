namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// An authorization request that <c>/connect/authorize</c> has fully validated, held
/// server-side for the duration of the consent hop.
/// <para>
/// This exists so the consent pages never re-derive the request from browser input. When the
/// parameters travelled as hidden form fields, every page they passed through had to
/// re-validate all of them against the application record — <c>redirect_uri</c>, PKCE,
/// scopes, <c>Enabled</c> — and each page that forgot one reopened the same hole. A consent
/// screen naming a trusted client but carrying an attacker's <c>redirect_uri</c> and
/// <c>code_challenge</c> is account takeover from a single click. Carrying only an opaque
/// handle removes the parameters from the browser's reach entirely, so there is nothing left
/// to tamper with and nothing for a future page to forget.
/// </para>
/// <para>
/// The record also carries <see cref="AuthorizationId"/>, so the code minted at the end of
/// the flow is linked to the consent that permitted it without threading it through as a
/// parameter — the omission that left the revocation cascade dead.
/// </para>
/// </summary>
public class OidcAuthorizationRequest
{
    /// <summary>SHA-256 of the <c>request_id</c>; see <see cref="Services.OidcRequestReference"/>.</summary>
    public string? Id { get; set; }

    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>The signed-in user the request was validated for. A different user presenting the handle is rejected.</summary>
    public string Subject { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>Requested scopes, already intersected with the application's allowed set.</summary>
    public List<string> Scopes { get; set; } = [];

    public string? CodeChallenge { get; set; }
    public string? CodeChallengeMethod { get; set; }
    public string? Nonce { get; set; }
    public string? State { get; set; }

    /// <summary>The <see cref="OidcAuthorization"/> this request is granted under; empty until consent is recorded.</summary>
    public string AuthorizationId { get; set; } = string.Empty;

    /// <summary>One of <c>pending</c>, <c>consumed</c>, <c>denied</c>.</summary>
    public string Status { get; set; } = "pending";

    public DateTime CreatedAt { get; set; }

    /// <summary>Bounds how long a consent screen stays live; expired requests are refused and may be swept.</summary>
    public DateTime ExpiresAt { get; set; }
}
