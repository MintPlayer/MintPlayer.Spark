using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// The lowest GitHub repository role whose holders may promote release lines with <c>/release</c> (segment P, P3;
/// <c>docs/release_lines_recovery_plan.md</c> §2b). Read from the forge on every request, never cached.
/// </summary>
/// <remarks>
/// ⚠️ <see cref="Maintain"/> is the zero value on purpose: it is P3's default, and an account written before the field
/// existed deserializes to it, with no migration. The dropdown's order comes from <see cref="ReleaseRole.Items"/>.
/// </remarks>
public enum EReleaseRole
{
    /// <summary>Holders of the maintain or admin role.</summary>
    Maintain,

    /// <summary>Holders of the admin role.</summary>
    Admin,

    /// <summary>Holders of the write, maintain or admin role.</summary>
    Write,
}

/// <summary>The same release role on a <c>Repository</c>, which may defer to its account.</summary>
public enum EReleaseRolePolicy
{
    /// <summary>Use the account's release role. The default for every repository.</summary>
    Inherit,

    Write,

    Maintain,

    Admin,
}

/// <summary>The selectable values of <see cref="EReleaseRole"/>.</summary>
public sealed class ReleaseRole : TransientLookupReference<EReleaseRole>
{
    private ReleaseRole() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<ReleaseRole> Items { get; } =
    [
        new ReleaseRole
        {
            Key = EReleaseRole.Write,
            Description = "Whoever has the write, maintain or admin role may promote the release lines",
            Values = _TS("Write role or higher", "Rôle write ou supérieur", "Rol write of hoger"),
        },
        new ReleaseRole
        {
            Key = EReleaseRole.Maintain,
            Description = "Whoever has the maintain or admin role may promote the release lines",
            Values = _TS("Maintain or admin role", "Rôle maintain ou admin", "Rol maintain of admin"),
        },
        new ReleaseRole
        {
            Key = EReleaseRole.Admin,
            Description = "Only whoever has the admin role may promote the release lines",
            Values = _TS("Admin role only", "Rôle admin uniquement", "Alleen de rol admin"),
        },
    ];
}

/// <summary>The selectable values of <see cref="EReleaseRolePolicy"/>.</summary>
public sealed class ReleaseRolePolicy : TransientLookupReference<EReleaseRolePolicy>
{
    private ReleaseRolePolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<ReleaseRolePolicy> Items { get; } =
    [
        new ReleaseRolePolicy
        {
            Key = EReleaseRolePolicy.Inherit,
            Description = "Use the account's release role",
            Values = _TS("Use account setting", "Utiliser le paramètre du compte", "Accountinstelling gebruiken"),
        },
        new ReleaseRolePolicy
        {
            Key = EReleaseRolePolicy.Write,
            Description = "Whoever has the write, maintain or admin role may promote the release lines",
            Values = _TS("Write role or higher", "Rôle write ou supérieur", "Rol write of hoger"),
        },
        new ReleaseRolePolicy
        {
            Key = EReleaseRolePolicy.Maintain,
            Description = "Whoever has the maintain or admin role may promote the release lines",
            Values = _TS("Maintain or admin role", "Rôle maintain ou admin", "Rol maintain of admin"),
        },
        new ReleaseRolePolicy
        {
            Key = EReleaseRolePolicy.Admin,
            Description = "Only whoever has the admin role may promote the release lines",
            Values = _TS("Admin role only", "Rôle admin uniquement", "Alleen de rol admin"),
        },
    ];
}
