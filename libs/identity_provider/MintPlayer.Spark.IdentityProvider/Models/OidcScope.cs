namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// Represents an OIDC scope definition.
/// Unifies IdentityServer's IdentityResource + ApiScope + ApiResource into a single entity.
/// </summary>
public class OidcScope
{
    /// <summary>Unique identifier of the scope, assigned automatically on creation.</summary>
    public string? Id { get; set; }
    /// <summary>Scope name a client requests, e.g. <c>openid</c> or <c>api.read</c>; must be unique.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Human-readable name of the scope, shown to users on the consent screen.</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Explanation of what the scope grants access to, shown to users on the consent screen.</summary>
    public string? Description { get; set; }
    /// <summary>User claim types included in the ID token when this scope is granted, e.g. <c>email</c>.</summary>
    public List<string> ClaimTypes { get; set; } = [];
    /// <summary>Audience values stamped into access tokens carrying this scope, naming the APIs that accept them.</summary>
    public List<string> Audiences { get; set; } = [];
    /// <summary>Whether the user must grant this scope and cannot untick it on the consent screen.</summary>
    public bool Required { get; set; }
    /// <summary>Whether the consent screen highlights this scope as sensitive.</summary>
    public bool Emphasize { get; set; }
    /// <summary>Whether the scope is listed publicly in the OpenID discovery document.</summary>
    public bool ShowInDiscoveryDocument { get; set; } = true;
    /// <summary>Whether the scope can currently be requested; disable to withdraw it without deleting it.</summary>
    public bool Enabled { get; set; } = true;
}
