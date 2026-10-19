using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

/// <summary>
/// The merge queue's approval floor on an <c>Account</c>: what the queue itself demands before it enqueues a pull
/// request, on top of whatever the repository's own rules demand (merge-queue PRD O11).
/// </summary>
/// <remarks>
/// ⚠️ <see cref="ExternalAuthors"/> is the zero value on purpose: it is O11's default, and an account document written
/// before the field existed deserializes to the zero value, so the default needs no migration.
/// </remarks>
public enum EMergeQueueApprovalFloor
{
    /// <summary>A pull request whose author lacks <c>write</c> needs one approval, on the pinned commit, by a <c>write</c> user.</summary>
    ExternalAuthors,

    /// <summary>No floor of the queue's own: the repository's rules (ruleset 2) decide alone.</summary>
    RepositoryRules,

    /// <summary>Every pull request needs one approval, on the pinned commit, by a <c>write</c> user other than its author.</summary>
    Always,
}

/// <summary>The same floor on a <c>Repository</c>, which may defer to its account (O11's per-repository override).</summary>
public enum EMergeQueueApprovalFloorPolicy
{
    /// <summary>Use the account's floor. The default for every repository.</summary>
    Inherit,

    ExternalAuthors,

    RepositoryRules,

    Always,
}

/// <summary>Whether a <c>Repository</c> uses its account's check exclusions or its own list (O12's per-repository override).</summary>
/// <remarks>
/// A named state rather than "a null list means inherit": the generic edit form cannot tell an empty list from an
/// absent one, so "exclude nothing here" and "use the account's list" would be indistinguishable (the same reason
/// <see cref="EDeleteBranchPolicy"/> is an enum).
/// </remarks>
public enum EMergeQueueCheckExclusionsPolicy
{
    /// <summary>Use the account's exclusion list. The default.</summary>
    Inherit,

    /// <summary>Use this repository's own list, even when it is empty.</summary>
    Override,
}

/// <summary>
/// The lowest GitHub repository role whose holders bypass the merge queue's own rules when they queue a pull request
/// (merge-queue PRD O22): the approval floor (O11), and the <c>.github/**</c> eviction of a same-repository pull request
/// that needs the regenerate workflow (O21 A). Re-checked right before the merge, like O9's role.
/// </summary>
/// <remarks>
/// <para>Never bypassed: the check gate (O12), the regenerate workflow's identity (D12), the patch validation (T4), the
/// lease and SHA pinning (T3), the enqueuer's own <c>write</c> minimum (O9), and anything for a fork's pull request.</para>
/// <para>⚠️ <see cref="None"/> is the zero value on purpose: an account written before the field existed bypasses
/// nothing, with no migration.</para>
/// </remarks>
public enum EMergeQueueBypassRole
{
    /// <summary>Nobody bypasses the queue's rules.</summary>
    None,

    /// <summary>Holders of the maintain or admin role.</summary>
    Maintain,

    /// <summary>Holders of the admin role.</summary>
    Admin,
}

/// <summary>The same bypass role on a <c>Repository</c>, which may defer to its account (O22's per-repository override).</summary>
public enum EMergeQueueBypassRolePolicy
{
    /// <summary>Use the account's bypass role. The default for every repository.</summary>
    Inherit,

    None,

    Maintain,

    Admin,
}

/// <summary>The selectable values of <see cref="EMergeQueueApprovalFloor"/>.</summary>
public sealed class MergeQueueApprovalFloor : TransientLookupReference<EMergeQueueApprovalFloor>
{
    private MergeQueueApprovalFloor() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<MergeQueueApprovalFloor> Items { get; } =
    [
        new MergeQueueApprovalFloor
        {
            Key = EMergeQueueApprovalFloor.ExternalAuthors,
            Description = "A pull request by an author without write access needs an approval by a writer",
            Values = _TS(("en", "Approval for external authors"), ("fr", "Approbation pour les auteurs externes"), ("nl", "Goedkeuring voor externe auteurs")),
        },
        new MergeQueueApprovalFloor
        {
            Key = EMergeQueueApprovalFloor.RepositoryRules,
            Description = "No floor of the queue's own; the repository's rules decide",
            Values = _TS(("en", "Repository rules only"), ("fr", "Règles du dépôt uniquement"), ("nl", "Alleen repositoryregels")),
        },
        new MergeQueueApprovalFloor
        {
            Key = EMergeQueueApprovalFloor.Always,
            Description = "Every pull request needs an approval by a writer other than its author",
            Values = _TS(("en", "Always require an approval"), ("fr", "Toujours exiger une approbation"), ("nl", "Altijd een goedkeuring vereisen")),
        },
    ];
}

/// <summary>The selectable values of <see cref="EMergeQueueApprovalFloorPolicy"/>.</summary>
public sealed class MergeQueueApprovalFloorPolicy : TransientLookupReference<EMergeQueueApprovalFloorPolicy>
{
    private MergeQueueApprovalFloorPolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<MergeQueueApprovalFloorPolicy> Items { get; } =
    [
        new MergeQueueApprovalFloorPolicy
        {
            Key = EMergeQueueApprovalFloorPolicy.Inherit,
            Description = "Use the account's approval floor",
            Values = _TS(("en", "Use account setting"), ("fr", "Utiliser le paramètre du compte"), ("nl", "Accountinstelling gebruiken")),
        },
        new MergeQueueApprovalFloorPolicy
        {
            Key = EMergeQueueApprovalFloorPolicy.ExternalAuthors,
            Description = "A pull request by an author without write access needs an approval by a writer",
            Values = _TS(("en", "Approval for external authors"), ("fr", "Approbation pour les auteurs externes"), ("nl", "Goedkeuring voor externe auteurs")),
        },
        new MergeQueueApprovalFloorPolicy
        {
            Key = EMergeQueueApprovalFloorPolicy.RepositoryRules,
            Description = "No floor of the queue's own; the repository's rules decide",
            Values = _TS(("en", "Repository rules only"), ("fr", "Règles du dépôt uniquement"), ("nl", "Alleen repositoryregels")),
        },
        new MergeQueueApprovalFloorPolicy
        {
            Key = EMergeQueueApprovalFloorPolicy.Always,
            Description = "Every pull request needs an approval by a writer other than its author",
            Values = _TS(("en", "Always require an approval"), ("fr", "Toujours exiger une approbation"), ("nl", "Altijd een goedkeuring vereisen")),
        },
    ];
}

/// <summary>The selectable values of <see cref="EMergeQueueBypassRole"/>.</summary>
public sealed class MergeQueueBypassRole : TransientLookupReference<EMergeQueueBypassRole>
{
    private MergeQueueBypassRole() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<MergeQueueBypassRole> Items { get; } =
    [
        new MergeQueueBypassRole
        {
            Key = EMergeQueueBypassRole.None,
            Description = "Nobody bypasses the merge queue's own rules",
            Values = _TS(("en", "Nobody"), ("fr", "Personne"), ("nl", "Niemand")),
        },
        new MergeQueueBypassRole
        {
            Key = EMergeQueueBypassRole.Maintain,
            Description = "Whoever queues with the maintain or admin role bypasses the approval floor and the .github eviction of a same-repository pull request",
            Values = _TS(("en", "Maintain or admin role"), ("fr", "Rôle maintain ou admin"), ("nl", "Rol maintain of admin")),
        },
        new MergeQueueBypassRole
        {
            Key = EMergeQueueBypassRole.Admin,
            Description = "Whoever queues with the admin role bypasses the approval floor and the .github eviction of a same-repository pull request",
            Values = _TS(("en", "Admin role only"), ("fr", "Rôle admin uniquement"), ("nl", "Alleen de rol admin")),
        },
    ];
}

/// <summary>The selectable values of <see cref="EMergeQueueBypassRolePolicy"/>.</summary>
public sealed class MergeQueueBypassRolePolicy : TransientLookupReference<EMergeQueueBypassRolePolicy>
{
    private MergeQueueBypassRolePolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<MergeQueueBypassRolePolicy> Items { get; } =
    [
        new MergeQueueBypassRolePolicy
        {
            Key = EMergeQueueBypassRolePolicy.Inherit,
            Description = "Use the account's bypass role",
            Values = _TS(("en", "Use account setting"), ("fr", "Utiliser le paramètre du compte"), ("nl", "Accountinstelling gebruiken")),
        },
        new MergeQueueBypassRolePolicy
        {
            Key = EMergeQueueBypassRolePolicy.None,
            Description = "Nobody bypasses the merge queue's own rules",
            Values = _TS(("en", "Nobody"), ("fr", "Personne"), ("nl", "Niemand")),
        },
        new MergeQueueBypassRolePolicy
        {
            Key = EMergeQueueBypassRolePolicy.Maintain,
            Description = "Whoever queues with the maintain or admin role bypasses the approval floor and the .github eviction of a same-repository pull request",
            Values = _TS(("en", "Maintain or admin role"), ("fr", "Rôle maintain ou admin"), ("nl", "Rol maintain of admin")),
        },
        new MergeQueueBypassRolePolicy
        {
            Key = EMergeQueueBypassRolePolicy.Admin,
            Description = "Whoever queues with the admin role bypasses the approval floor and the .github eviction of a same-repository pull request",
            Values = _TS(("en", "Admin role only"), ("fr", "Rôle admin uniquement"), ("nl", "Alleen de rol admin")),
        },
    ];
}

/// <summary>The selectable values of <see cref="EMergeQueueCheckExclusionsPolicy"/>.</summary>
public sealed class MergeQueueCheckExclusionsPolicy : TransientLookupReference<EMergeQueueCheckExclusionsPolicy>
{
    private MergeQueueCheckExclusionsPolicy() { }

    public override ELookupDisplayType DisplayType => ELookupDisplayType.Dropdown;

    public static IReadOnlyCollection<MergeQueueCheckExclusionsPolicy> Items { get; } =
    [
        new MergeQueueCheckExclusionsPolicy
        {
            Key = EMergeQueueCheckExclusionsPolicy.Inherit,
            Description = "Use the account's check exclusions",
            Values = _TS(("en", "Use account setting"), ("fr", "Utiliser le paramètre du compte"), ("nl", "Accountinstelling gebruiken")),
        },
        new MergeQueueCheckExclusionsPolicy
        {
            Key = EMergeQueueCheckExclusionsPolicy.Override,
            Description = "Use this repository's own check exclusions",
            Values = _TS(("en", "This repository's list"), ("fr", "La liste de ce dépôt"), ("nl", "De lijst van deze repository")),
        },
    ];
}
