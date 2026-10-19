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
[SparkLookup("OrganizationRoles")]
public enum OrganizationRole
{
    /// <summary>Reads only.</summary>
    Viewer = 1,
    /// <summary>Works with the organization's content.</summary>
    Member = 2,
    /// <summary>Manages members, invitations and content.</summary>
    Admin = 3,
    /// <summary>Everything, including deleting the organization.</summary>
    Owner = 4,
}

// The role dropdown of the membership and invitation forms is the generated OrganizationRoles; the labels are the
// translations.json keys lookup.OrganizationRole.{Member} (commerce X20 item 5).

/// <summary>
/// The role dropdown of the invite form: every role but Owner, which is transferred, never mailed. A subset of
/// <see cref="OrganizationRoles"/>, with its labels.
/// </summary>
public sealed class OrganizationInviteRoles : TransientLookupReference<OrganizationRole>
{
    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;
    private OrganizationInviteRoles() { }

    public static IReadOnlyCollection<OrganizationInviteRoles> Items { get; } =
    [
        new OrganizationInviteRoles { Key = OrganizationRole.Viewer, Values = TranslatedString.FromKey("lookup.OrganizationRole.Viewer") },
        new OrganizationInviteRoles { Key = OrganizationRole.Member, Values = TranslatedString.FromKey("lookup.OrganizationRole.Member") },
        new OrganizationInviteRoles { Key = OrganizationRole.Admin, Values = TranslatedString.FromKey("lookup.OrganizationRole.Admin") },
    ];
}
