using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Organizations.Models;

/// <summary>
/// A user's role in an organization (<c>docs/seo_analyzer_PRD.md</c> D23). One document per (user,
/// organization), with the fixed id <see cref="IdFor"/>: a user's memberships are read by id prefix,
/// straight from storage (no index, so a membership is effective the moment it is saved; §12 "Spike S13").
/// </summary>
/// <remarks>
/// Members see each other (Viewer and up); Admins change roles and remove members, Owners alone touch
/// an Owner. Memberships are created by the package only: on creating an organization, on accepting an
/// invitation or a join request. The last Owner can neither leave nor be removed or demoted.
/// </remarks>
[RequiresOrganizationRole("New", OrganizationRole.Admin)]
[RequiresOrganizationRole("Edit", OrganizationRole.Admin)]
[RequiresOrganizationRole("Delete", OrganizationRole.Admin)]
[RequiresOrganizationRole(OrganizationActionNames.TransferOwnership, OrganizationRole.Owner)]
[IdKind(SparkIdKind.Natural)]
public partial class OrganizationMembership : IOrganizationOwned
{
    /// <summary><see cref="IdFor"/>.</summary>
    public Id<OrganizationMembership> Id { get; set; }

    /// <inheritdoc />
    public string? OrganizationId { get; set; }

    /// <summary>The member's user id.</summary>
    public string? UserId { get; set; }

    /// <summary>The member's user name when they joined, shown on the members list. Never the address.</summary>
    public string? UserName { get; set; }

    /// <summary>The member's role.</summary>
    public OrganizationRole Role { get; set; } = OrganizationRole.Viewer;

    /// <summary>When the user joined.</summary>
    public DateTime? JoinedAtUtc { get; set; }

    /// <summary>The id of <paramref name="userId"/>'s membership of <paramref name="organizationId"/>.</summary>
    public static Id<OrganizationMembership> IdFor(string userId, string organizationId) => Id<OrganizationMembership>.FromTrusted(PrefixFor(userId) + organizationId);

    /// <inheritdoc cref="IdFor(string, string)"/>
    public static Id<OrganizationMembership> IdFor(string userId, Id<Organization> organizationId) => IdFor(userId, organizationId.Value);

    /// <summary>
    /// The id prefix of every membership of <paramref name="userId"/>. Memberships stored before #536 keep their
    /// mixed casing; RavenDB ids and prefix loads ignore case.
    /// </summary>
    public static string PrefixFor(string userId) => "organizationmemberships/" + userId + "/";
}
