using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Organizations.Models;

/// <summary>
/// An organization (<c>docs/seo_analyzer_PRD.md</c> X11, D18, D21): a named group of users with roles
/// (<see cref="OrganizationMembership"/>) that owns documents carrying its id
/// (<see cref="IOrganizationOwned"/>). Every user also has a <see cref="IsPersonal">personal</see> one,
/// created on first sign-in, that they own for good and others can join (D42).
/// </summary>
/// <remarks>
/// Who sees an organization: its members (Viewer and up). Who renames it: Admin and up. Who deletes it:
/// its Owners, and never a personal one (that goes with the account). Anyone holding
/// <c>New/Organization</c> creates one and becomes its Owner.
/// </remarks>
[RequiresOrganizationRole("Edit", OrganizationRole.Admin)]
[RequiresOrganizationRole("Delete", OrganizationRole.Owner)]
[RequiresOrganizationRole(OrganizationActionNames.Invite, OrganizationRole.Admin)]
[RequiresOrganizationRole(OrganizationActionNames.Leave, OrganizationRole.Viewer)]
[IdKind(SparkIdKind.Natural)]
public partial class Organization
{
    /// <summary>
    /// <c>organizations/{guid}</c>, or <see cref="PersonalIdFor"/> for a personal organization. Organizations created
    /// before #536 keep their <c>Organizations/</c> casing, in their own id and in every <c>OrganizationId</c> that
    /// refers to them: compare organization ids ignoring case.
    /// </summary>
    public Id<Organization> Id { get; set; }

    /// <summary>The name members see.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>A user's own organization: they are its only Owner, others join it like any other (D42), deleted with the account.</summary>
    public bool IsPersonal { get; set; }

    /// <summary>The user who created it (for a personal organization, its user and only Owner).</summary>
    public string? CreatedBy { get; set; }

    /// <summary>When it was created.</summary>
    public DateTime? CreatedAtUtc { get; set; }

    /// <summary>The id of <paramref name="userId"/>'s personal organization: fixed, so creating it twice is a no-op.</summary>
    public static Id<Organization> PersonalIdFor(string userId) => Id<Organization>.FromTrusted("organizations/personal/" + userId);

    /// <summary>A new id for a shared organization.</summary>
    public static Id<Organization> NewId() => Id<Organization>.FromTrusted("organizations/" + Guid.NewGuid().ToString("N"));
}

/// <summary>The package's custom actions (<c>App_Data/actions.json</c>).</summary>
public static class OrganizationActionNames
{
    /// <summary>On an organization's page: invite an address (Admin and up).</summary>
    public const string Invite = "InviteOrganizationMember";

    /// <summary>On an organization's page: leave it (any member but the last Owner).</summary>
    public const string Leave = "LeaveOrganization";

    /// <summary>On a member row: make that member an Owner, the caller becoming Admin (Owners only).</summary>
    public const string TransferOwnership = "TransferOrganizationOwnership";
}
