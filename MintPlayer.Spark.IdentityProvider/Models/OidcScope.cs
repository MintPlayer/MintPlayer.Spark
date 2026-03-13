namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// Represents an OIDC scope definition.
/// This model is used internally by the IdentityProvider endpoints to read
/// OidcScope documents from RavenDB.
/// </summary>
public class OidcScope
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Description { get; set; }
    public List<string> ClaimTypes { get; set; } = [];
    public bool Required { get; set; }
}
