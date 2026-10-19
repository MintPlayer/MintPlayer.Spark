using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.LookupReferences;

// Each [SparkLookup] enum generates the lookup of the same name without its E prefix (EMergeQueueApprovalFloor →
// MergeQueueApprovalFloor); the labels are the translations.json keys lookup.{Enum}.{Member}.

/// <summary>
/// The merge queue's approval floor on an <c>Account</c>: what the queue itself demands before it enqueues a pull
/// request, on top of whatever the repository's own rules demand (merge-queue PRD O11).
/// </summary>
/// <remarks>
/// ⚠️ <see cref="ExternalAuthors"/> is the zero value on purpose: it is O11's default, and an account document written
/// before the field existed deserializes to the zero value, so the default needs no migration.
/// </remarks>
[SparkLookup]
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
[SparkLookup]
public enum EMergeQueueApprovalFloorPolicy
{
    /// <summary>Use the account's floor. The default for every repository.</summary>
    Inherit,

    /// <summary>A pull request by an author without write access needs an approval by a writer.</summary>
    ExternalAuthors,

    /// <summary>No floor of the queue's own; the repository's rules decide.</summary>
    RepositoryRules,

    /// <summary>Every pull request needs an approval by a writer other than its author.</summary>
    Always,
}

/// <summary>Whether a <c>Repository</c> uses its account's check exclusions or its own list (O12's per-repository override).</summary>
/// <remarks>
/// A named state rather than "a null list means inherit": the generic edit form cannot tell an empty list from an
/// absent one, so "exclude nothing here" and "use the account's list" would be indistinguishable (the same reason
/// <see cref="EDeleteBranchPolicy"/> is an enum).
/// </remarks>
[SparkLookup]
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
[SparkLookup]
public enum EMergeQueueBypassRole
{
    /// <summary>Nobody bypasses the queue's rules.</summary>
    None,

    /// <summary>Holders of the maintain or admin role: they bypass the approval floor and the .github eviction of a same-repository pull request.</summary>
    Maintain,

    /// <summary>Holders of the admin role: they bypass the approval floor and the .github eviction of a same-repository pull request.</summary>
    Admin,
}

/// <summary>The same bypass role on a <c>Repository</c>, which may defer to its account (O22's per-repository override).</summary>
[SparkLookup]
public enum EMergeQueueBypassRolePolicy
{
    /// <summary>Use the account's bypass role. The default for every repository.</summary>
    Inherit,

    /// <summary>Nobody bypasses the merge queue's own rules.</summary>
    None,

    /// <summary>Whoever queues with the maintain or admin role bypasses the approval floor and the .github eviction of a same-repository pull request.</summary>
    Maintain,

    /// <summary>Whoever queues with the admin role bypasses the approval floor and the .github eviction of a same-repository pull request.</summary>
    Admin,
}
