using System.Reflection;

namespace MintPlayer.Spark.Organizations;

/// <summary>
/// A document that belongs to an organization (<c>docs/seo_analyzer_PRD.md</c> X11, §4.2). Implementing
/// it is the whole row rule: the package's row policy limits every path that asks row security to the
/// members of <see cref="OrganizationId"/> holding the role the action requires
/// (<see cref="RequiresOrganizationRoleAttribute"/>; Viewer to read, Member to create and edit, Admin to
/// delete by default). Anyone else gets the missing-row 404 and never sees the row in a list (#453).
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <see cref="OrganizationId"/> must be a public <c>string?</c> property named exactly that: the policy's
/// predicate is rebound onto each type by member name. A row without one belongs to nobody and is visible
/// to nobody. On a create the row must name an organization the caller holds the <c>New</c> role in
/// (WITH CHECK). A create through the generic pipeline that leaves it empty gets the organization chosen in
/// the switcher, else the caller's personal one (<c>Interceptors/OrganizationOwnedDefaults</c>).
/// </para>
/// <para>
/// Raw session queries are not filtered: background code that acts for a user asks
/// <see cref="ISparkOrganizations.GetMembershipsAsync"/> itself.
/// </para>
/// </remarks>
public interface IOrganizationOwned
{
    /// <summary>The id of the owning <see cref="Models.Organization"/>.</summary>
    string? OrganizationId { get; set; }
}

/// <summary>
/// An organization-owned document that its organization can <b>publish</b> (<c>docs/commerce_PRD.md</c> X8): while
/// <see cref="IsPublished"/> is true, everyone may <c>Query</c> and <c>Read</c> it, anonymous callers included.
/// Every other action stays with the organization's members, as for any <see cref="IOrganizationOwned"/> row.
/// </summary>
/// <remarks>
/// <para>
/// <b>The OR is inside the organization policy only.</b> For <c>Query</c>/<c>Read</c> the policy's predicate is
/// <c>OrganizationId in (my organizations) || IsPublished == true</c>; core still ANDs that with the type's own
/// rule and every other policy (expiration, soft deletion, moderation), so a published row that expired, was
/// deleted or is hidden stays hidden. Publishing widens who may read; it never lifts another rule.
/// </para>
/// <para>
/// Type rights still apply first: anonymous visitors reach a published row only if <c>security.json</c> grants
/// their group <c>Query</c>/<c>Read</c> on the type.
/// </para>
/// <para>
/// Flipping <see cref="IsPublished"/> (or creating a row published, or moving a published row to another
/// organization) needs the <c>Publish</c> role in the row's organization, which follows <c>Edit</c> unless declared:
/// <c>[RequiresOrganizationRole("Publish", OrganizationRole.Admin)]</c>.
/// </para>
/// <para>
/// ⚠️ <see cref="IsPublished"/> must be a public <c>bool</c> named exactly that (rebound by member name). A
/// document without the field is not published (<c>IsPublished = true</c> does not match an absent field), the
/// safe direction. A list with an organization chosen in the switcher shows that organization's rows only,
/// published rows of others included only under "All organizations".
/// </para>
/// </remarks>
public interface IOrganizationPublishable : IOrganizationOwned
{
    /// <summary>Whether everyone may list and read this row.</summary>
    bool IsPublished { get; set; }
}

/// <summary>
/// The organization role an action on this type requires, overriding the default
/// (<c>Query</c>/<c>Read</c>: <see cref="OrganizationRole.Viewer"/>; <c>New</c>/<c>Edit</c> and custom
/// actions: <see cref="OrganizationRole.Member"/>; <c>Delete</c>: <see cref="OrganizationRole.Admin"/>).
/// Repeat it per action. <c>Query</c> and <c>Read</c> follow each other when only one is declared,
/// <c>Restore</c>/<c>Revert</c>/<c>Publish</c> follow <c>Edit</c>, <c>Purge</c> follows <c>Delete</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Custom roles</b> (<c>docs/commerce_PRD.md</c> X8): an action can also name a role the host or a library
/// declared (<c>spark.AddOrganizationRole("scanner", …)</c>), which a member holds beside their built-in role
/// (<see cref="Models.OrganizationMembership.CustomRoles"/>). The declarations of one action are alternatives:
/// a member passes with the built-in role (or higher), or with any of the named custom roles. An action that
/// names only custom roles is open to their holders alone. An Owner passes every action.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [RequiresOrganizationRole("New", OrganizationRole.Admin)]
/// [RequiresOrganizationRole("StartAudit", OrganizationRole.Member)]
/// [RequiresOrganizationRole("Scan", "scanner")]                       // holders of "scanner" (and Owners)
/// [RequiresOrganizationRole("Refund", OrganizationRole.Admin)]
/// [RequiresOrganizationRole("Refund", "finance")]                      // Admins, or holders of "finance"
/// public class Site : IOrganizationOwned { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequiresOrganizationRoleAttribute : Attribute
{
    /// <summary>The lowest built-in role that may perform <paramref name="action"/>.</summary>
    public RequiresOrganizationRoleAttribute(string action, OrganizationRole role)
    {
        Action = action;
        Role = role;
    }

    /// <summary>A custom role (declared with <c>AddOrganizationRole</c>) whose holders may perform <paramref name="action"/>.</summary>
    public RequiresOrganizationRoleAttribute(string action, string customRole)
    {
        Action = action;
        CustomRole = customRole;
    }

    /// <summary><c>Query</c>, <c>Read</c>, <c>New</c>, <c>Edit</c>, <c>Delete</c>, <c>Publish</c>, or a custom action's name.</summary>
    public string Action { get; }

    /// <summary>The lowest built-in role that may perform <see cref="Action"/>; null when this declaration names a custom role.</summary>
    public OrganizationRole? Role { get; }

    /// <summary>The custom role that may perform <see cref="Action"/>; null when this declaration names a built-in role.</summary>
    public string? CustomRole { get; }

    /// <summary>What <paramref name="action"/> requires on <paramref name="type"/>: its declarations, else the default.</summary>
    public static OrganizationRequirement For(Type type, string action)
    {
        var declared = type.GetCustomAttributes<RequiresOrganizationRoleAttribute>(inherit: true).ToList();
        List<RequiresOrganizationRoleAttribute> Declared(string name)
            => declared.Where(a => Is(a.Action, name)).ToList();
        List<RequiresOrganizationRoleAttribute> FirstDeclared(params string[] names)
            => names.Select(Declared).FirstOrDefault(d => d.Count > 0) ?? [];

        var own = Declared(action);
        if (own.Count > 0)
            return From(own);

        return action switch
        {
            // Read and Query go together unless both are declared: a list must not hide what a load shows.
            _ when Is(action, "Query") || Is(action, "Read") => From(FirstDeclared("Read", "Query"), OrganizationRole.Viewer),
            _ when Is(action, "Restore") || Is(action, "Revert") || Is(action, "Publish") => From(Declared("Edit"), OrganizationRole.Member),
            _ when Is(action, "Delete") => OrganizationRequirement.AtLeast(OrganizationRole.Admin),
            _ when Is(action, "Purge") => From(Declared("Delete"), OrganizationRole.Admin),
            _ => OrganizationRequirement.AtLeast(OrganizationRole.Member),
        };
    }

    /// <summary>The requirement <paramref name="declarations"/> state, or <paramref name="fallback"/> when there are none.</summary>
    private static OrganizationRequirement From(List<RequiresOrganizationRoleAttribute> declarations, OrganizationRole fallback = OrganizationRole.Member)
    {
        if (declarations.Count == 0)
            return OrganizationRequirement.AtLeast(fallback);
        var role = declarations.FirstOrDefault(d => d.Role is not null)?.Role;
        var custom = declarations
            .Where(d => !string.IsNullOrWhiteSpace(d.CustomRole))
            .Select(d => d.CustomRole!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new OrganizationRequirement(role, custom);
    }

    private static bool Is(string action, string name) => string.Equals(action, name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What an action requires of a member (<see cref="RequiresOrganizationRoleAttribute.For"/>): a built-in role or
/// higher, or one of the custom roles. An Owner always passes.
/// </summary>
/// <param name="Role">The lowest built-in role that passes; null when only <paramref name="CustomRoles"/> do.</param>
/// <param name="CustomRoles">Custom roles whose holders pass, whatever their built-in role.</param>
public sealed record OrganizationRequirement(OrganizationRole? Role, IReadOnlyList<string> CustomRoles)
{
    /// <summary>The requirement of <paramref name="role"/> or higher, no custom role.</summary>
    public static OrganizationRequirement AtLeast(OrganizationRole role) => new(role, []);

    /// <summary>Whether a member with <paramref name="role"/> and <paramref name="customRoles"/> passes.</summary>
    public bool IsMetBy(OrganizationRole role, IEnumerable<string>? customRoles)
        => role == OrganizationRole.Owner
            || (Role is { } minimum && role >= minimum)
            || (customRoles is not null && CustomRoles.Count > 0
                && customRoles.Any(held => CustomRoles.Contains(held, StringComparer.OrdinalIgnoreCase)));
}
