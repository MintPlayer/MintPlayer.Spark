using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Organizations;

/// <summary>
/// A member's role in an organization (<c>docs/seo_analyzer_PRD.md</c> D23), ordered: each role can do
/// everything the roles below it can. The package compares roles; what each one may do in an
/// application is the application's to map, through <see cref="RequiresOrganizationRoleAttribute"/>.
/// </summary>
/// <remarks>
/// The conventional meaning (D23): <see cref="Owner"/> everything, including deleting the organization
/// and transferring ownership; <see cref="Admin"/> manages members, invitations, join requests and the
/// organization's content; <see cref="Member"/> works with the content; <see cref="Viewer"/> reads only.
/// </remarks>
public enum OrganizationRole
{
    Viewer = 1,
    Member = 2,
    Admin = 3,
    Owner = 4,
}

/// <summary>The role dropdown of the membership and invitation forms.</summary>
public sealed class OrganizationRoles : TransientLookupReference<OrganizationRole>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OrganizationRoles() { }

    public static IReadOnlyCollection<OrganizationRoles> Items { get; } =
    [
        new OrganizationRoles { Key = OrganizationRole.Viewer, Description = "Reads only", Values = _TS(("en", "Viewer"), ("fr", "Lecteur"), ("nl", "Lezer")) },
        new OrganizationRoles { Key = OrganizationRole.Member, Description = "Works with the organization's content", Values = _TS(("en", "Member"), ("fr", "Membre"), ("nl", "Lid")) },
        new OrganizationRoles { Key = OrganizationRole.Admin, Description = "Manages members, invitations and content", Values = _TS(("en", "Admin"), ("fr", "Administrateur"), ("nl", "Beheerder")) },
        new OrganizationRoles { Key = OrganizationRole.Owner, Description = "Everything, including deleting the organization", Values = _TS(("en", "Owner"), ("fr", "Propriétaire"), ("nl", "Eigenaar")) },
    ];
}

/// <summary>The role dropdown of the invite form: every role but Owner, which is transferred, never mailed.</summary>
public sealed class OrganizationInviteRoles : TransientLookupReference<OrganizationRole>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OrganizationInviteRoles() { }

    public static IReadOnlyCollection<OrganizationInviteRoles> Items { get; } =
    [
        new OrganizationInviteRoles { Key = OrganizationRole.Viewer, Description = "Reads only", Values = _TS(("en", "Viewer"), ("fr", "Lecteur"), ("nl", "Lezer")) },
        new OrganizationInviteRoles { Key = OrganizationRole.Member, Description = "Works with the organization's content", Values = _TS(("en", "Member"), ("fr", "Membre"), ("nl", "Lid")) },
        new OrganizationInviteRoles { Key = OrganizationRole.Admin, Description = "Manages members, invitations and content", Values = _TS(("en", "Admin"), ("fr", "Administrateur"), ("nl", "Beheerder")) },
    ];
}
