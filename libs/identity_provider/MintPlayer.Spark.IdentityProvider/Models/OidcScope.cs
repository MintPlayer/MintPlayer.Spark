namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// Represents an OIDC scope definition.
/// Unifies IdentityServer's IdentityResource + ApiScope + ApiResource into a single entity.
/// </summary>
public class OidcScope
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> ClaimTypes { get; set; } = [];
    public List<string> Audiences { get; set; } = [];
    public bool Required { get; set; }
    public bool Emphasize { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public bool Enabled { get; set; } = true;
}
