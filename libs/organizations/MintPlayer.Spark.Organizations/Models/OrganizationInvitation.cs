using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Organizations.Models;

/// <summary>What an <see cref="OrganizationInvitation"/> asks.</summary>
public enum OrganizationInvitationKind
{
    /// <summary>An Admin invited an address; whoever opens the mailed link signed in joins.</summary>
    Invitation = 1,

    /// <summary>A user asked to join; an Owner or Admin who opens the mailed link lets them in.</summary>
    JoinRequest = 2,
}

/// <summary>
/// A pending invitation or join request (<c>docs/seo_analyzer_PRD.md</c> D18). The mailed link carries
/// a single-use token; only its SHA-256 is stored. Accepting deletes the document. It expires after the
/// model's <c>expiresAfter</c> (14 days, X1): an expired one is hidden everywhere and refused.
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

    /// <summary>An invitation or a join request.</summary>
    public OrganizationInvitationKind Kind { get; set; } = OrganizationInvitationKind.Invitation;

    /// <summary>The address the link was mailed to (an invitation), or the requester's address (a join request).</summary>
    public string? Email { get; set; }

    /// <summary>A join request's requester: the user who joins when it is accepted.</summary>
    public string? UserId { get; set; }

    /// <summary>A join request's requester's user name.</summary>
    public string? UserName { get; set; }

    /// <summary>The role the new member gets. Never <see cref="OrganizationRole.Owner"/>: ownership is transferred, not given away by mail.</summary>
    public OrganizationRole Role { get; set; } = OrganizationRole.Member;

    /// <summary>Lower-case hex SHA-256 of the mailed token. The token itself is never stored.</summary>
    public string? TokenHash { get; set; }

    /// <summary>The user who invited (an invitation) or asked (a join request).</summary>
    public string? CreatedBy { get; set; }

    /// <summary>When it was sent.</summary>
    public DateTime? CreatedAtUtc { get; set; }

    /// <inheritdoc />
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>A new id.</summary>
    public static string NewId() => "OrganizationInvitations/" + Guid.NewGuid().ToString("N");
}
