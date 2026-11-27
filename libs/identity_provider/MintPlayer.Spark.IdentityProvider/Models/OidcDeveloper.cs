using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.IdentityProvider.Models;

/// <summary>
/// A user's developer status (<c>docs/identity_provider_platform_PRD.md</c> D1, D2): the <c>OidcDevelopers</c> collection,
/// one document per user, at an id derived from the user's (<see cref="Services.OidcDevelopers.DocumentId"/>), so a load
/// answers a membership decision consistently.
/// <para>
/// Until #536 (D47) the status was a <c>Developer</c> field patched into the user's own document, which only RavenDB
/// keeps for a type that has no property for it. The migration <c>M_202611251300_OidcDevelopersCollection</c> moves each
/// such field into a document of this collection and removes it from the user.
/// </para>
/// </summary>
[IdKind(SparkIdKind.Natural)]
public partial class OidcDeveloper
{
    /// <summary><c>oidcdevelopers/&lt;hash of the user id&gt;</c>.</summary>
    public Id<OidcDeveloper> Id { get; set; }
    /// <summary>The user this status belongs to.</summary>
    public string UserId { get; set; } = string.Empty;
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
