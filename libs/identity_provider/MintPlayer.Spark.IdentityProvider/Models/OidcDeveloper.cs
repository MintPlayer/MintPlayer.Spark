namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// A user's developer status (<c>docs/identity_provider_platform_PRD.md</c> D1, D2). Stored as the
/// <see cref="FieldName"/> field of the user's own document, not in a collection of its own: the
/// identity provider patches it in and reads it with a projection, so the user type needs no
/// property for it. RavenDB keeps the field across every load and save of the user
/// (<c>PreserveDocumentPropertiesNotFoundOnModel</c>, measured true in RavenDB.Client 7.2.6).
/// </summary>
public class OidcDeveloper
{
    /// <summary>The field on the user document.</summary>
    public const string FieldName = "Developer";

    /// <summary><c>Requested</c>, <c>Approved</c>, <c>Rejected</c> or <c>Revoked</c>.</summary>
    public string Status { get; set; } = OidcDeveloperStatuses.Requested;
    /// <summary>The version of the developer terms the user accepted.</summary>
    public int TermsVersion { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>The administrator who approved or rejected the request; null when no approval was required.</summary>
    public string? DecidedBy { get; set; }
    /// <summary>The reason given with a rejection.</summary>
    public string? Note { get; set; }
}

public static class OidcDeveloperStatuses
{
    public const string Requested = "Requested";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Revoked = "Revoked";
}
