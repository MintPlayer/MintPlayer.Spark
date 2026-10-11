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
/// The organization role an action on this type requires, overriding the default
/// (<c>Query</c>/<c>Read</c>: <see cref="OrganizationRole.Viewer"/>; <c>New</c>/<c>Edit</c> and custom
/// actions: <see cref="OrganizationRole.Member"/>; <c>Delete</c>: <see cref="OrganizationRole.Admin"/>).
/// Repeat it per action. <c>Query</c> and <c>Read</c> follow each other when only one is declared,
/// <c>Restore</c>/<c>Revert</c> follow <c>Edit</c>, <c>Purge</c> follows <c>Delete</c>.
/// </summary>
/// <example>
/// <code>
/// [RequiresOrganizationRole("New", OrganizationRole.Admin)]
/// [RequiresOrganizationRole("StartAudit", OrganizationRole.Member)]
/// public class Site : IOrganizationOwned { … }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequiresOrganizationRoleAttribute(string action, OrganizationRole role) : Attribute
{
    /// <summary><c>Query</c>, <c>Read</c>, <c>New</c>, <c>Edit</c>, <c>Delete</c>, or a custom action's name.</summary>
    public string Action { get; } = action;

    /// <summary>The lowest role that may perform <see cref="Action"/>.</summary>
    public OrganizationRole Role { get; } = role;

    /// <summary>The role <paramref name="action"/> requires on <paramref name="type"/>: its attribute, else the default.</summary>
    public static OrganizationRole For(Type type, string action)
    {
        var declared = type.GetCustomAttributes<RequiresOrganizationRoleAttribute>(inherit: true).ToList();
        OrganizationRole? Declared(string name)
            => declared.FirstOrDefault(a => string.Equals(a.Action, name, StringComparison.OrdinalIgnoreCase))?.Role;

        if (Declared(action) is { } role)
            return role;

        return action switch
        {
            // Read and Query go together unless both are declared: a list must not hide what a load shows.
            _ when Is(action, "Query") || Is(action, "Read") => Declared("Read") ?? Declared("Query") ?? OrganizationRole.Viewer,
            _ when Is(action, "Restore") || Is(action, "Revert") => Declared("Edit") ?? OrganizationRole.Member,
            _ when Is(action, "Delete") => OrganizationRole.Admin,
            _ when Is(action, "Purge") => Declared("Delete") ?? OrganizationRole.Admin,
            _ => OrganizationRole.Member,
        };
    }

    private static bool Is(string action, string name) => string.Equals(action, name, StringComparison.OrdinalIgnoreCase);
}
