namespace MintPlayer.Spark.IdentityProvider.Models;

public class OidcToken
{
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = "";
    public string AuthorizationId { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Type { get; set; } = "";
    public string? ReferenceId { get; set; }
    public string? CodeChallenge { get; set; }
    public string? CodeChallengeMethod { get; set; }
    public string? RedirectUri { get; set; }
    public List<string> Scopes { get; set; } = [];
    public string? Payload { get; set; }
    public string Status { get; set; } = "valid";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RedeemedAt { get; set; }
    public string? State { get; set; }
}
