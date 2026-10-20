using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Organizations.Models;

/// <summary>
/// A pending invitation (<c>docs/seo_analyzer_PRD.md</c> D18): an Admin invited an address, and whoever opens the
/// mailed link signed in with that address joins. The link carries a single-use token; only its SHA-256 is stored.
/// Accepting deletes the document. It expires after the model's <c>expiresAfter</c> (14 days, X1): an expired one is
/// hidden everywhere and refused. Join requests are approval requests since D26 (<see cref="OrganizationJoinApproval"/>).
/// </summary>
/// <remarks>Admins and up see and revoke them (delete); nobody edits one.</remarks>
[RequiresOrganizationRole("Query", OrganizationRole.Admin)]
[RequiresOrganizationRole("New", OrganizationRole.Admin)]
[RequiresOrganizationRole("Edit", OrganizationRole.Owner)]
[RequiresOrganizationRole("Delete", OrganizationRole.Admin)]
public class OrganizationInvitation : IOrganizationOwned, IExpiring
{
    /// <summary><c>OrganizationInvitations/{guid}</c>.</summary>
    public string? Id { get; set; }

    /// <inheritdoc />
    public string? OrganizationId { get; set; }

    /// <summary>The address the link was mailed to.</summary>
    public string? Email { get; set; }

    /// <summary>The role the new member gets. Never <see cref="OrganizationRole.Owner"/>: ownership is transferred, not given away by mail.</summary>
    public OrganizationRole Role { get; set; } = OrganizationRole.Member;

    /// <summary>Lower-case hex SHA-256 of the mailed token. The token itself is never stored.</summary>
    public string? TokenHash { get; set; }

    /// <summary>The user who invited.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>When it was sent.</summary>
    public DateTime? CreatedAtUtc { get; set; }

    /// <inheritdoc />
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>A new id.</summary>
    public static string NewId() => "OrganizationInvitations/" + Guid.NewGuid().ToString("N");
}
