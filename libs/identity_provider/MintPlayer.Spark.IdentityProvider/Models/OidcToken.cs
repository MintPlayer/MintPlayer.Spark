namespace MintPlayer.Spark.IdentityProvider.Models;

public class OidcToken
{
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string AuthorizationId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    // No ReferenceId: the bearer value is never persisted. The document id is its SHA-256
    // (see OidcTokenReference), so lookups are point-loads and a database leak yields
    // nothing replayable.
    public string? CodeChallenge { get; set; }
    public string? CodeChallengeMethod { get; set; }
    public string? RedirectUri { get; set; }
    public List<string> Scopes { get; set; } = [];
    // No Payload: the signed JWT was stored in cleartext, written three times and read never.
    // Access-token records are keyed by the token's jti instead (see AccessTokens), which is
    // what makes them revocable — storing the token bought nothing but a liability.
    public string Status { get; set; } = "valid";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public string? State { get; set; }
}
