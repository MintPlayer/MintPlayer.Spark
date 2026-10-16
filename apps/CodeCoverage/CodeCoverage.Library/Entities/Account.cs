using CodeCoverage.Forge;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A GitHub user or organization that owns repositories. Created/updated from
/// GitHub App installation webhooks; document id is Accounts/{GitHubId} so
/// webhook upserts are idempotent.
/// </summary>
[GenerateIndex]
public class Account : IForgeConnectable
{
    /// <summary>Document id of this account, <c>Accounts/{GitHubId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>GitHub's numeric id for this user or organization; stable even when the login is renamed.</summary>
    /// <remarks>
    /// [IgnoreProperty]: entity-only (forge API and document ids), filtered by no index query, so it
    /// stays out of the model, the wire and the generated index (#264).
    /// </remarks>
    [IgnoreProperty]
    public long GitHubId { get; set; }

    /// <summary>The GitHub login (user name or organization slug) of this account.</summary>
    public string Login { get; set; } = string.Empty;

    /// <summary>"User" or "Organization".</summary>
    public string Type { get; set; } = "User";

    /// <summary>URL of the account's GitHub avatar image, shown next to the login.</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>GitHub App installation on this account, when the app is installed.</summary>
    /// <remarks>
    /// ⚠️ <b>GitHub's mechanism, not the fact.</b> Ask <see cref="Connection"/> whether we can act
    /// on this account — GitLab grants access with a group token and Bitbucket with a workspace
    /// credential, and neither has an installation id to put here. A neutral caller reading this
    /// field is a neutral caller that only works for one forge.
    /// <para>
    /// [IgnoreProperty]: operational detail no page draws and no index query filters on, so it never
    /// reaches the wire at all — which replaces the per-row withholding AccountActions used to do (#264).
    /// </para>
    /// </remarks>
    [IgnoreProperty]
    public long? InstallationId { get; set; }

    /// <summary>Whether we can still act on this account's repositories.</summary>
    /// <remarks>
    /// <para>
    /// The neutral answer to "is this account connected", which three callers ask and two of them
    /// ask <em>inside a RavenDB query</em> — the account sweep in the reconciliation cron job and
    /// the manual resync. That is why it is a stored field rather than a method on
    /// <c>IForgeIntegration</c>: an interface call cannot be pushed into RQL, and the alternative is
    /// every caller reading <see cref="InstallationId"/> and thereby naming GitHub.
    /// </para>
    /// <para>
    /// ⚠️ A <b>reachability flag, not a credential.</b> What it means to be connected is each
    /// forge's business and each forge writes it; what a neutral caller needs is only whether we
    /// still are. A neutral layer must never hold the credential itself.
    /// </para>
    /// <para>
    /// Defaults to <see cref="RepositoryConnection.Connected"/>, which is what every document
    /// written before this field existed deserializes to — and, for those, correct: an account only
    /// existed because an installation created it.
    /// </para>
    /// </remarks>
    public RepositoryConnection Connection { get; set; } = RepositoryConnection.Connected;

    /// <summary>Why the account is disconnected, from <see cref="DisconnectedReasons"/>; null while connected.</summary>
    public string? DisconnectedReason { get; set; }

    /// <summary>When the account was last disconnected (UTC); null while connected.</summary>
    public DateTime? DisconnectedAtUtc { get; set; }

    /// <summary>
    /// Account-wide default: delete a merged pull request's head branch, in every repository of
    /// this account that does not decide for itself.
    /// </summary>
    /// <remarks>
    /// A plain <see langword="bool"/> rather than a three-state policy, deliberately: this is the
    /// bottom of the chain, so there is nothing left to defer to. A repository overrides it with
    /// <see cref="EDeleteBranchPolicy.Enabled"/> or <see cref="EDeleteBranchPolicy.Disabled"/>, and
    /// inherits it with <see cref="EDeleteBranchPolicy.Inherit"/> — see
    /// <see cref="Repository.ResolveDeleteBranchOnPrClose"/>, which is the only place the two are
    /// combined.
    /// <para>
    /// Off by default. Deleting a branch is the one irreversible thing this app does to somebody
    /// else's repository, so it is opted into rather than out of.
    /// </para>
    /// </remarks>
    public bool DeleteBranchOnPrClose { get; set; }

    /// <summary>
    /// What the merge queue requires in approvals before it queues a pull request, in every repository of this account
    /// that does not set its own.
    /// </summary>
    /// <remarks>
    /// Merge-queue PRD O11; a repository overrides it with <see cref="Repository.MergeQueueApprovalFloor"/>. Trust policy, so it lives here and not in the repository's <c>coverage.yml</c>, which a pull request could
    /// touch (O15). Only the account's owner may change it: <c>AccountActions</c> narrows writes to managed owners.
    /// <para>[IgnoreForIndex]: owner-facing configuration has no business in the anonymous account grid.</para>
    /// </remarks>
    [IgnoreForIndex]
    [LookupReference(typeof(LookupReferences.MergeQueueApprovalFloor))]
    public LookupReferences.EMergeQueueApprovalFloor MergeQueueApprovalFloor { get; set; }

    /// <summary>
    /// Check names the merge queue does not wait for, in every repository of this account that does not set its own
    /// list. A check a branch rule requires is always waited for.
    /// </summary>
    /// <remarks>
    /// Merge-queue PRD O12, e.g. a scanner known to report false positives on generated files.
    /// [IgnoreForIndex]: see <see cref="MergeQueueApprovalFloor"/>.
    /// <para>
    /// Picked from the checks that reported on any repository of this account (§11, O38, D110). The stored value stays
    /// the name, and a name no longer known stays as it is (D114).
    /// </para>
    /// </remarks>
    [IgnoreForIndex]
    [Reference(typeof(ForgeCheck), "Account_ForgeChecks")]
    public List<string> MergeQueueCheckExclusions { get; set; } = [];

    /// <summary>
    /// The lowest GitHub repository role whose holders bypass the merge queue's own rules when they queue a pull request,
    /// in every repository of this account that does not set its own.
    /// </summary>
    /// <remarks>
    /// Merge-queue PRD O22; a repository overrides it with <see cref="Repository.MergeQueueBypassRole"/>. What it skips and
    /// what it never skips: <see cref="LookupReferences.EMergeQueueBypassRole"/>. Nobody by default.
    /// [IgnoreForIndex]: see <see cref="MergeQueueApprovalFloor"/>.
    /// </remarks>
    [IgnoreForIndex]
    [LookupReference(typeof(LookupReferences.MergeQueueBypassRole))]
    public LookupReferences.EMergeQueueBypassRole MergeQueueBypassRole { get; set; }

    /// <summary>
    /// Keep a rolling dependency-update pull request in every repository of this account that does not decide for itself.
    /// </summary>
    /// <remarks>
    /// Dependency-updates PRD §4.1 (G2); a repository overrides it with <see cref="Repository.DependencyUpdates"/>, and its
    /// <c>coverage.yml</c> may only opt out (Q6). Off by default: the updater pushes branches into somebody else's
    /// repository. [IgnoreForIndex]: owner-facing configuration, like <see cref="MergeQueueApprovalFloor"/>.
    /// </remarks>
    [IgnoreForIndex]
    public bool DependencyUpdates { get; set; }

    /// <summary>How often the dependency updater recomputes the rolling pull request (PRD §4.1, Q8).</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(LookupReferences.DependencyScheduleFrequency))]
    public LookupReferences.EDependencyScheduleFrequency DependencyScheduleFrequency { get; set; }

    /// <summary>The weekday of a weekly or monthly slot (Q8).</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(LookupReferences.DependencyWeekday))]
    public LookupReferences.EDependencyWeekday DependencyScheduleWeekday { get; set; }

    /// <summary>The hour of the slot, 0–23, in <see cref="DependencyTimeZone"/>; runs are spread across that hour (Q8).</summary>
    [IgnoreForIndex]
    public int DependencyScheduleHour { get; set; } = DefaultDependencyScheduleHour;

    /// <summary>The IANA time zone of the schedule (<c>Europe/Brussels</c>); an unknown id is read as UTC.</summary>
    [IgnoreForIndex]
    public string DependencyTimeZone { get; set; } = "UTC";

    /// <summary>How old a third-party release must be before it is planned (Q16). Own packages are exempt (§7.6).</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(LookupReferences.DependencyCooldown))]
    public LookupReferences.EDependencyCooldown DependencyCooldown { get; set; }

    /// <summary>How many CI runs a red rolling pull request may spend on bisecting (Q12, §7.4).</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(LookupReferences.DependencyBisectBudget))]
    public LookupReferences.EDependencyBisectBudget DependencyBisectBudget { get; set; }

    /// <summary>
    /// Account-wide dependency rules (§4.2, Q9). An enforced rule is a ceiling every repository's <c>coverage.yml</c> stays
    /// under (R6); see <see cref="DependencyRule"/>.
    /// </summary>
    [IgnoreForIndex]
    public List<DependencyRule> DependencyRules { get; set; } = [];

    /// <summary>Q8's default hour.</summary>
    public const int DefaultDependencyScheduleHour = 6;

    /// <summary>
    /// The forge that hosts this account. Set on every document by the M6 re-key.
    /// </summary>
    /// <remarks>
    /// Stored rather than parsed back out of the id, because callers that hold an entity should not
    /// have to re-derive what the entity already knows — and because the id is a storage detail
    /// while this is a fact about the account.
    /// </remarks>
    public EForgeProvider Provider { get; set; } = EForgeProvider.GitHub;

    /// <summary><see cref="Provider"/> and <c>Login</c> as one comparable value, <c>github:acme</c>.</summary>
    /// <remarks>See <see cref="Repository.OwnerKey"/> for why this is a field of its own.</remarks>
    /// <remarks>Derived — see <see cref="Repository.OwnerKey"/> for why it is not settable.</remarks>
    public string OwnerKey => new Forge.ForgeOwner(Provider, Login ?? string.Empty).ToString();

    /// <summary><c>Accounts/{provider}/{accountId}</c>.</summary>
    /// <remarks>
    /// The provider segment is required because a numeric account id is only unique <em>within</em>
    /// a forge — GitHub user 1234 and GitLab group 1234 are different accounts (D25).
    /// </remarks>
    public static string DocumentId(EForgeProvider provider, long accountId)
        => $"Accounts/{provider.ToCanonicalString()}/{accountId}";
}
