using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Apps.Models;

/// <summary>
/// An App (D5, D29): what a developer registers so other users can install it. Its identity is an
/// <c>OidcApplication</c> of the site's identity provider (a <c>client_credentials</c> client that signs a
/// <c>private_key_jwt</c> with one of <see cref="Keys"/>); its webhook is a <see cref="WebhookEndpoint"/> with
/// <see cref="WebhookEndpoint.AppSlug"/> set. Id <c>sparkapps/{slug}</c>, so a slug is unique by construction.
/// </summary>
[IdKind(SparkIdKind.Natural)]
public sealed partial class SparkApp
{
    public const string IdPrefix = "sparkapps/";

    public Id<SparkApp> Id { get; set; }
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

    /// <summary>Its installations (<see cref="AppInstallation"/> ids), so the App owner lists them without an index.</summary>
    public List<string> InstallationIds { get; set; } = [];

    public DateTime CreatedUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public WebhookOwner Owner => new(OwnerKind, OwnerKey);

    public static Id<SparkApp> IdFor(string slug) => Id<SparkApp>.FromTrusted(IdPrefix + slug);
}

/// <summary>
/// An App installed on a target (a user or an organization, D30): what the installer granted it. Id
/// <c>appinstallations/in_…</c>; the <c>in_…</c> part is the public installation id the App sends with its token request.
/// One per (App, target): the target's <see cref="WebhookOwnerRegistry.InstallationIds"/> is checked under optimistic
/// concurrency.
/// </summary>
[IdKind(SparkIdKind.Natural)]
public sealed partial class AppInstallation
{
    public const string IdPrefix = "appinstallations/";

    public Id<AppInstallation> Id { get; set; }
    public string AppSlug { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string TargetKey { get; set; } = string.Empty;

    /// <summary>
    /// What the installer accepted. Never more than the App asks for: when the App asks for more, the installation keeps
    /// these until the installer accepts again; when it asks for less, these shrink at once.
    /// </summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>Suspended by the installer: no events, no new tokens, and the tokens it holds stop working.</summary>
    public bool Suspended { get; set; }
    public DateTime? SuspendedUtc { get; set; }

    /// <summary>The identity provider grant behind its tokens (D16): withdrawn on suspension, revoked on uninstall.</summary>
    public string GrantId { get; set; } = string.Empty;

    public string InstalledBy { get; set; } = string.Empty;
    public DateTime InstalledUtc { get; set; }

    public WebhookOwner Target => new(TargetKind, TargetKey);

    public string PublicId => Id.IsEmpty ? string.Empty : Id.Value[IdPrefix.Length..];

    /// <summary>The installation with public id <paramref name="publicId"/>.</summary>
    public static Id<AppInstallation> IdFor(string publicId) => Id<AppInstallation>.FromTrusted(IdPrefix + publicId);
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
