namespace MintPlayer.Spark.Apps.Models;

/// <summary>
/// An App (D5, D29): what a developer registers so other users can install it. Its identity is an
/// <c>OidcApplication</c> of the site's identity provider (a <c>client_credentials</c> client that signs a
/// <c>private_key_jwt</c> with one of <see cref="Keys"/>); its webhook is a <see cref="WebhookEndpoint"/> with
/// <see cref="WebhookEndpoint.AppSlug"/> set. Id <c>SparkApps/{slug}</c>, so a slug is unique by construction.
/// </summary>
public sealed class SparkApp
{
    public const string IdPrefix = "SparkApps/";

    public string? Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? HomepageUrl { get; set; }

    public string OwnerKind { get; set; } = string.Empty;
    public string OwnerKey { get; set; } = string.Empty;

    /// <summary>The identity provider's <c>OidcApplications/…</c> document.</summary>
    public string OidcApplicationId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The App's webhook (<c>ep_…</c>), or null when it has no webhook URL.</summary>
    public string? WebhookEndpointId { get; set; }

    /// <summary>What installing the App grants it, <c>resource:read</c> or <c>resource:write</c> (write implies read).</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>The events it receives from its installations; each needs its permission among <see cref="Permissions"/>.</summary>
    public List<string> Events { get; set; } = [];

    /// <summary>Whether anyone may install it, or only its owner.</summary>
    public bool Public { get; set; }

    /// <summary>Where the installer goes after installing (GitHub's setup URL).</summary>
    public string? SetupUrl { get; set; }

    public List<SparkAppKey> Keys { get; set; } = [];

    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public WebhookOwner Owner => new(OwnerKind, OwnerKey);

    public static string IdFor(string slug) => IdPrefix + slug;
}

/// <summary>The public half of a key the App signs its client assertions with. The private half was shown once.</summary>
public sealed class SparkAppKey
{
    /// <summary>The JWK <c>kid</c>.</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>SHA-256 of the SubjectPublicKeyInfo, base64, so the developer can match it to the file they keep.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>The public JWK as JSON (RS256).</summary>
    public string PublicJwk { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}
