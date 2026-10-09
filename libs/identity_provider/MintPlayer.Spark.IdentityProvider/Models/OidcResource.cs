using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// What a client can ask for: the <c>OidcResources</c> collection
/// (<c>docs/identity_provider_platform_PRD.md</c> D1), IdentityServer's IdentityResource, ApiResource
/// and ApiScope in one document type.
/// <list type="bullet">
/// <item>An <b>identity resource</b> (<see cref="OidcResourceKinds.Identity"/>) is itself one scope,
/// <c>openid</c>, <c>profile</c>, <c>email</c>, with the user claims it releases.</item>
/// <item>An <b>API resource</b> (<see cref="OidcResourceKinds.Api"/>) is an audience. Its
/// <see cref="Name"/> is the <c>aud</c> its access tokens carry, and its scopes are embedded in
/// <see cref="Scopes"/>, each prefixed with the resource's name (<c>fleet.read</c>).</item>
/// </list>
/// The document id is natural, <c>OidcResources/&lt;name&gt;</c>, and a scope name is unique across
/// every resource (a compare-exchange reservation, O17).
/// </summary>
public class OidcResource
{
    /// <summary>The document id, <c>OidcResources/&lt;name&gt;</c>.</summary>
    public string? Id { get; set; }
    /// <summary><c>Identity</c> for a scope of user claims, or <c>Api</c> for a protected API.</summary>
    public string Kind { get; set; } = OidcResourceKinds.Identity;
    /// <summary>For an identity resource, the scope name (<c>email</c>). For an API, the audience (<c>fleet</c>). Read-only once created.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Human-readable name, shown to users on the consent screen.</summary>
    public TranslatedString? DisplayName { get; set; }
    /// <summary>What it grants access to, shown to users on the consent screen.</summary>
    public TranslatedString? Description { get; set; }
    /// <summary>Whether it can currently be requested; disable to withdraw it without deleting it.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Whether it is listed publicly in the OpenID discovery document.</summary>
    public bool ShowInDiscoveryDocument { get; set; } = true;

    // --- Identity resources ---
    /// <summary>User claim types released when this scope is granted, e.g. <c>email</c>.</summary>
    public List<string> ClaimTypes { get; set; } = [];
    /// <summary>Whether the user must grant this scope and cannot untick it on the consent screen.</summary>
    public bool Required { get; set; }
    /// <summary>Whether the consent screen highlights this scope as sensitive.</summary>
    public bool Emphasize { get; set; }

    // --- API resources ---
    /// <summary>The API's scopes. Each name starts with the resource's name and a dot.</summary>
    public List<OidcApiScope> Scopes { get; set; } = [];
    /// <summary>The users who own the API. Their applications get its scopes without approval; other applications need theirs (D4).</summary>
    public List<string> Owners { get; set; } = [];
    /// <summary>Whether any application may request its scopes without an owner's approval.</summary>
    public bool AutoApprove { get; set; }
    /// <summary>Whether the API itself may introspect the tokens addressed to it.</summary>
    public bool AllowIntrospection { get; set; } = true;
    /// <summary>The user who created the resource.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>The scope names this resource defines: its own name for an identity resource, its scopes' names for an API.</summary>
    public IEnumerable<string> ScopeNames()
        => Kind == OidcResourceKinds.Api ? Scopes.Select(s => s.Name) : [Name];
}

public static class OidcResourceKinds
{
    /// <summary>A scope of user claims: <c>openid</c>, <c>profile</c>, <c>email</c>.</summary>
    public const string Identity = "Identity";
    /// <summary>A protected API, named by its audience.</summary>
    public const string Api = "Api";
}

/// <summary>One scope of an API resource, e.g. <c>fleet.read</c>.</summary>
[ValueObject]
public partial class OidcApiScope
{
    /// <summary>The scope name a client requests, starting with the resource's name and a dot.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Human-readable name, shown to users on the consent screen.</summary>
    public TranslatedString? DisplayName { get; set; }
    /// <summary>What it grants access to, shown to users on the consent screen.</summary>
    public TranslatedString? Description { get; set; }
    /// <summary>User claim types added to the access token when this scope is granted.</summary>
    public List<string> ClaimTypes { get; set; } = [];
    /// <summary>Whether the consent screen highlights this scope as sensitive.</summary>
    public bool Emphasize { get; set; }
    /// <summary>Whether it can currently be requested.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Whether it is listed publicly in the OpenID discovery document.</summary>
    public bool ShowInDiscoveryDocument { get; set; } = true;
}

/// <summary>
/// One scope with everything a token, a consent screen or discovery needs to know about it,
/// flattened from its <see cref="OidcResource"/>. Built by <c>OidcScopeCatalog</c>, never stored.
/// </summary>
public sealed record OidcScopeDefinition(
    string Name,
    string ResourceName,
    string ResourceKind,
    TranslatedString? DisplayName,
    TranslatedString? Description,
    IReadOnlyList<string> ClaimTypes,
    bool Required,
    bool Emphasize,
    bool ShowInDiscoveryDocument)
{
    /// <summary>The audience an access token carrying this scope is addressed to; none for an identity scope.</summary>
    public string? Audience => ResourceKind == OidcResourceKinds.Api ? ResourceName : null;

    /// <summary>The display name in <paramref name="culture"/>, falling back to the scope name.</summary>
    public string DisplayNameIn(string culture)
    {
        var value = DisplayName?.GetValue(culture);
        return string.IsNullOrEmpty(value) ? Name : value;
    }
}
