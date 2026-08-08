namespace MintPlayer.Spark.IdentityProvider.Models;

public class OidcAuthorization
{
    public string? Id { get; set; }
    public string ApplicationId { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = "valid";
    public List<string> GrantedScopes { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
