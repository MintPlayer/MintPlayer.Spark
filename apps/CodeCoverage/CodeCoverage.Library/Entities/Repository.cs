using CodeCoverage.Forge;
using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A GitHub repository the app knows about (installed via the GitHub App).
/// Document id is Repositories/{GitHubId} so webhook upserts are idempotent.
/// </summary>
[GenerateIndex]
public class Repository : IForgeConnectable
{
    /// <summary>Document id of this repository, <c>Repositories/{GitHubId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>The GitHub user or organization that owns this repository.</summary>
    [Reference(typeof(Account))]
    public string? Account { get; set; }

    /// <summary>GitHub's numeric id for this repository.</summary>
    /// <remarks>
    /// Stable across renames and transfers.
    /// <para>
    /// [IgnoreProperty]: an entity-side key for the forge API and the document ids, never shown and
    /// filtered by no index query, so it stays out of the model, the wire and the generated index (#264).
    /// </para>
    /// </remarks>
    [IgnoreProperty]
    public long GitHubId { get; set; }

    /// <summary>
    /// The forge that hosts this repository. Set on every document by the M6 re-key.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is what replaces <c>ForgeIntegrationResolver.ProviderOf</c>, which returned
    /// <see cref="EForgeProvider.GitHub"/> unconditionally as an explicit placeholder until M6.
    /// <para>
    /// [IgnoreForIndex] because index membership is opt-out: without it this lands in VRepository
    /// and synchronize adds a column to the /spark repository grid, which security.json grants to
    /// Everyone. It is a routing discriminator, not something the grid is about — and a guard test
    /// caught it appearing there, which is the guard working.
    /// </para>
    /// </remarks>
    [IgnoreForIndex]
    public EForgeProvider Provider { get; set; } = EForgeProvider.GitHub;

    /// <summary>
    /// <see cref="Provider"/> and <see cref="OwnerLogin"/> as one comparable value,
    /// <c>github:mintplayer</c> — the form authorization filters on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>A separate field rather than a rewrite of <see cref="OwnerLogin"/>, which is what the
    /// plan originally called for.</b> Two reasons, and the second is the deciding one.
    /// </para>
    /// <para>
    /// First, <c>OwnerLogin</c> is what URLs are built from — <c>/api/repos/{owner}/{repo}</c> — and
    /// several lookups compare it against a route segment. Qualifying it in place would break every
    /// one of those the moment the migration ran, and they cannot be fixed until routes carry the
    /// provider (M7). Adding a field keeps the two concerns independent: the login stays the
    /// human-readable, URL-shaped thing it always was, and the key is what decides access.
    /// </para>
    /// <para>
    /// Second, an owner set flattened to bare logins <b>silently unions forges</b>: a GitLab user
    /// named <c>mintplayer</c> would inherit the GitHub <c>mintplayer</c>'s repositories. That is
    /// the bug this field exists to make unrepresentable, and it is a query-shaped problem — the
    /// row filters are <c>IN</c> clauses — so the answer has to be a single stored comparable value
    /// rather than a pair of fields compared in application code.
    /// </para>
    /// <para>
    /// A colon, not a slash: GitLab namespaces nest and are themselves slash-delimited, so a slash
    /// could not be split back apart. See <see cref="Forge.ForgeOwner"/>. This deliberately differs
    /// from the spelling used in document ids and must not be "tidied" to match.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// ⚠️ <b>Derived, not settable.</b> A settable field is one every write path can forget, and
    /// forgetting it here does not fail — it produces a repository that no owner filter matches,
    /// which reads as "the grid is empty" rather than as a bug. Computing it from the two fields it
    /// summarises makes the two impossible to disagree. RavenDB serialises the getter, so it is
    /// stored and indexed exactly as a field would be; the migration still backfills documents
    /// written before it existed, because their stored JSON has no such property.
    /// </remarks>
    public string OwnerKey => new Forge.ForgeOwner(Provider, OwnerLogin).ToString();

    /// <summary>The repository name without the owner, e.g. <c>MintPlayer.Spark</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>owner/name</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>GitHub login of the owning user or organization.</summary>
    /// <remarks>The part before the slash in the full name.</remarks>
    public string OwnerLogin { get; set; } = string.Empty;

    /// <summary>Whether the repository is private on GitHub.</summary>
    /// <remarks>Private repositories need a badge token for their badge.</remarks>
    public bool IsPrivate { get; set; }

    /// <summary>The repository's default branch on GitHub.</summary>
    /// <remarks>Its newest finalized build supplies the headline coverage.</remarks>
    public string? DefaultBranch { get; set; }

    /// <summary>Whether the repository has been archived on GitHub.</summary>
    /// <remarks>An archived repository no longer receives uploads.</remarks>
    public bool Archived { get; set; }

    /// <summary>
    /// Whether a merged pull request's head branch is deleted — or whether that is left to the
    /// owning <see cref="Account"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="EDeleteBranchPolicy.Inherit"/>, so a repository says nothing until
    /// somebody makes it say something. Deleting a branch is still opted into rather than out of:
    /// the account's own default is off.
    /// <para>
    /// ⚠️ An earlier version of this remark said the flag "cannot be a global default" because it
    /// mutates other people's repositories. That reasoning ruled out a <em>server-wide</em> default
    /// and was right; it does not rule out an <em>account-wide</em> one, which is scoped to
    /// repositories the account already owns. The account level is the "later" that decision D1 of
    /// the project-automation plan deferred.
    /// </para>
    /// <para>
    /// ⚠️ Three states, not a <see langword="bool"/>?. A nullable bool cannot be edited through the
    /// generic form: a lookup does not change an attribute's <c>dataType</c>, so it renders as a
    /// two-state checkbox, and the empty-string key its "unset" row would produce is silently
    /// discarded by the mapper. <see cref="EDeleteBranchPolicy"/> gives each state a name and a
    /// translated label. See <c>docs/coverage_branch_deletion_setting_PRD.md</c>.
    /// </para>
    /// <para>
    /// Lives here rather than on <see cref="GitHubProject"/>, where it was first declared. A board
    /// and a repository are siblings under an <see cref="Account"/>, and deleting a ref is a
    /// repository operation that no board takes part in: gating it on a board made it unreachable
    /// for an owner with no board, inert for a board carrying no pull-request rule, and duplicated
    /// for an owner with two boards. See the decision register (C16) for the reversal of A4/D3.
    /// </para>
    /// <para>
    /// Merged pull requests only — a pull request closed without merging keeps its branch, because
    /// the work on it has not landed anywhere.
    /// </para>
    /// </remarks>
    [LookupReference(typeof(DeleteBranchPolicy))]
    public EDeleteBranchPolicy DeleteBranchOnPrClose { get; set; }

    /// <summary>
    /// The effective answer for one repository: its own policy, or its account's default when it
    /// defers.
    /// </summary>
    /// <remarks>
    /// The single place the two levels are combined, so the webhook path and any settings UI cannot
    /// drift. A missing account resolves to <see langword="false"/> — the safe direction for an
    /// irreversible operation.
    /// </remarks>
    /// <summary>How many former names one repository remembers, oldest dropped first.</summary>
    public const int MaxPreviousFullNames = 16;

    /// <summary>
    /// Records the name this repository is about to stop being known by.
    /// </summary>
    /// <remarks>
    /// The name we knew a repository by is baked into every published badge URL, so a rename or a
    /// transfer is the moment to remember it — afterwards the old name is unrecoverable.
    /// <para>
    /// Lives on the entity rather than on a webhook recipient because it is a rule about what a
    /// repository remembers, and both the forge-specific normaliser and the neutral rename handler
    /// need it. Two copies of a capped, de-duplicated list are two chances to cap it differently.
    /// </para>
    /// </remarks>
    public void RememberPreviousFullName(string newFullName)
    {
        if (FullName != newFullName)
            RecordPreviousFullName(FullName);
    }

    /// <summary>
    /// Records a name this repository was known by, given explicitly.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>For a caller that KNOWS the old name rather than one that can infer it.</b>
    /// <see cref="RememberPreviousFullName"/> derives it from <see cref="FullName"/>, which only
    /// works while the document still says the old thing — and the neutral rename handler runs
    /// after a forge library has already upserted the new one from the same payload. Inferring
    /// there would record the name the repository already has, which is not an alias.
    /// <para>
    /// Both entry points share this body, because two copies of a capped, de-duplicated list are
    /// two chances to cap it differently.
    /// </para>
    /// </remarks>
    public void RecordPreviousFullName(string? previousFullName)
    {
        if (string.IsNullOrEmpty(previousFullName)) return;
        if (PreviousFullNames.Contains(previousFullName, StringComparer.OrdinalIgnoreCase)) return;

        PreviousFullNames.Add(previousFullName);
        if (PreviousFullNames.Count > MaxPreviousFullNames)
            PreviousFullNames.RemoveAt(0);
    }

    /// <summary>Whether this repository labels its pull requests by size, or the account's setting (docs/coverage_pr_size_labels_PRD.md D1).</summary>
    /// <remarks>A stored document without the field reads <see cref="EPrSizeLabelPolicy.Inherit"/>, the enum's zero.</remarks>
    [IgnoreForIndex]
    [LookupReference(typeof(PrSizeLabelPolicy))]
    public EPrSizeLabelPolicy PrSizeLabels { get; set; }

    /// <summary>This repository's own ignore rules, applied after the account's, so the last matching rule still wins (D5).</summary>
    [IgnoreForIndex]
    public List<PrSizeIgnoreRule> PrSizeIgnoreRules { get; set; } = [];

    /// <summary>What the app last wrote to the forge per scale row (D3). Internal state, read-only and never shown.</summary>
    [IgnoreForIndex]
    public List<ManagedLabel> PrSizeLabelState { get; set; } = [];

    /// <summary>
    /// Whether pull requests in this repository get a size label, and where the answer came from (<c>repository</c>,
    /// <c>organization</c> or <c>default</c>). The one place the two levels are combined; a missing account is off.
    /// </summary>
    public static (bool Enabled, string Source) ResolvePrSizeLabels(Repository? repository, Account? account)
        => repository?.PrSizeLabels switch
        {
            EPrSizeLabelPolicy.Enabled => (true, "repository"),
            EPrSizeLabelPolicy.Disabled => (false, "repository"),
            _ => (account?.PrSizeLabels ?? false, account is null ? "default" : "organization"),
        };

    public static bool ResolveDeleteBranchOnPrClose(Repository? repository, Account? account)
        => repository?.DeleteBranchOnPrClose switch
        {
            EDeleteBranchPolicy.Enabled => true,
            EDeleteBranchPolicy.Disabled => false,
            _ => account?.DeleteBranchOnPrClose ?? false,
        };

    /// <summary>
    /// What the merge queue requires in approvals before it queues a pull request in this repository, or the account's
    /// setting.
    /// </summary>
    /// <remarks>
    /// Merge-queue PRD O11. Trust policy edited by the owner, never read from <c>coverage.yml</c> (O15). [IgnoreForIndex]: owner-facing
    /// configuration stays out of the anonymous /spark grid, like <see cref="Gate"/>.
    /// </remarks>
    [IgnoreForIndex]
    [LookupReference(typeof(MergeQueueApprovalFloorPolicy))]
    public EMergeQueueApprovalFloorPolicy MergeQueueApprovalFloor { get; set; }

    /// <summary>Whether this repository uses the account's list of checks the merge queue ignores, or its own.</summary>
    /// <remarks>Merge-queue PRD O12: <see cref="MergeQueueCheckExclusions"/> replaces the account's list when overridden.</remarks>
    [IgnoreForIndex]
    [LookupReference(typeof(MergeQueueCheckExclusionsPolicy))]
    public EMergeQueueCheckExclusionsPolicy MergeQueueCheckExclusionsPolicy { get; set; }

    /// <summary>
    /// This repository's own list of checks the merge queue ignores, used instead of the account's when chosen. A check a
    /// branch rule requires is always waited for.
    /// </summary>
    /// <remarks>
    /// Picked from <see cref="KnownChecks"/> (merge-queue PRD §11, D110). The stored value stays the name, and a name no
    /// longer known stays as it is (D114).
    /// </remarks>
    [IgnoreForIndex]
    [Reference(typeof(ForgeCheck), "Repository_ForgeChecks")]
    public List<string> MergeQueueCheckExclusions { get; set; } = [];

    /// <summary>
    /// The checks that have reported on this repository at least once: the options of the merge queue's exclusion lists
    /// (merge-queue PRD §11, O38, D107).
    /// </summary>
    /// <remarks>
    /// Written by <c>KnownChecksRecorder</c> only when a name is new or a day stale, capped at
    /// <see cref="ForgeCheck.MaxKnown"/>. [IgnoreProperty]: a cache behind the picker, never an attribute of its own, so it
    /// stays out of <c>Repository.json</c> and off the wire (the <c>Repository_ForgeChecks</c> query serves it to managers
    /// only). [IgnoreForIndex]: check names of a private repository have no business in the anonymous /spark grid.
    /// </remarks>
    [IgnoreProperty]
    [IgnoreForIndex]
    public List<ForgeCheck> KnownChecks { get; set; } = [];

    /// <summary>
    /// The lowest GitHub repository role whose holders bypass the merge queue's own rules when they queue a pull request in
    /// this repository, or the account's setting.
    /// </summary>
    /// <remarks>Merge-queue PRD O22: what it skips and what it never skips, <see cref="EMergeQueueBypassRole"/>.</remarks>
    [IgnoreForIndex]
    [LookupReference(typeof(MergeQueueBypassRolePolicy))]
    public EMergeQueueBypassRolePolicy MergeQueueBypassRole { get; set; }

    /// <summary>
    /// The effective approval floor and where it came from (<c>repository</c> or <c>organization</c>), the one place
    /// the two levels are combined (O11, O15). A missing account resolves to O11's default.
    /// </summary>
    public static (EMergeQueueApprovalFloor Floor, string Source) ResolveMergeQueueApprovalFloor(Repository? repository, Account? account)
        => repository?.MergeQueueApprovalFloor switch
        {
            EMergeQueueApprovalFloorPolicy.ExternalAuthors => (EMergeQueueApprovalFloor.ExternalAuthors, "repository"),
            EMergeQueueApprovalFloorPolicy.RepositoryRules => (EMergeQueueApprovalFloor.RepositoryRules, "repository"),
            EMergeQueueApprovalFloorPolicy.Always => (EMergeQueueApprovalFloor.Always, "repository"),
            _ => (account?.MergeQueueApprovalFloor ?? EMergeQueueApprovalFloor.ExternalAuthors, account is null ? "default" : "organization"),
        };

    /// <summary>
    /// The effective bypass role and where it came from (<c>repository</c>, <c>organization</c> or <c>default</c>), the one
    /// place the two levels are combined (O22, O15). A missing account resolves to nobody.
    /// </summary>
    public static (EMergeQueueBypassRole Role, string Source) ResolveMergeQueueBypassRole(Repository? repository, Account? account)
        => repository?.MergeQueueBypassRole switch
        {
            EMergeQueueBypassRolePolicy.None => (EMergeQueueBypassRole.None, "repository"),
            EMergeQueueBypassRolePolicy.Maintain => (EMergeQueueBypassRole.Maintain, "repository"),
            EMergeQueueBypassRolePolicy.Admin => (EMergeQueueBypassRole.Admin, "repository"),
            _ => (account?.MergeQueueBypassRole ?? EMergeQueueBypassRole.None, account is null ? "default" : "organization"),
        };

    /// <summary>Whether this repository keeps a rolling dependency-update pull request, or the account's setting (PRD §4.1, G2).</summary>
    /// <remarks>Its <c>coverage.yml</c> may still opt out (<c>dependencies.enabled: false</c>, Q6), never in.</remarks>
    [IgnoreForIndex]
    [LookupReference(typeof(DependencyUpdatesPolicy))]
    public EDependencyUpdatesPolicy DependencyUpdates { get; set; }

    /// <summary>Whether this repository uses the account's dependency-update schedule or its own (Q8).</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(DependencySchedulePolicy))]
    public EDependencySchedulePolicy DependencySchedulePolicy { get; set; }

    /// <summary>This repository's own frequency, used when <see cref="DependencySchedulePolicy"/> overrides.</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(DependencyScheduleFrequency))]
    public EDependencyScheduleFrequency DependencyScheduleFrequency { get; set; }

    /// <summary>This repository's own weekday, used when <see cref="DependencySchedulePolicy"/> overrides.</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(DependencyWeekday))]
    public EDependencyWeekday DependencyScheduleWeekday { get; set; }

    /// <summary>This repository's own hour (0–23, in the account's time zone), used when <see cref="DependencySchedulePolicy"/> overrides.</summary>
    [IgnoreForIndex]
    public int DependencyScheduleHour { get; set; } = CodeCoverage.Entities.Account.DefaultDependencyScheduleHour;

    /// <summary>This repository's cooldown, or the account's (Q16).</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(DependencyCooldownPolicy))]
    public EDependencyCooldownPolicy DependencyCooldown { get; set; }

    /// <summary>This repository's bisect budget, or the account's (Q12).</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(DependencyBisectBudgetPolicy))]
    public EDependencyBisectBudgetPolicy DependencyBisectBudget { get; set; }

    /// <summary>Whether this repository's onboarding pull request turns the merge queue on, or the account's choice (R26).</summary>
    [IgnoreForIndex]
    [LookupReference(typeof(DependencyOnboardingMergeQueuePolicy))]
    public EDependencyOnboardingMergeQueuePolicy DependencyOnboardingMergeQueue { get; set; }

    /// <summary>
    /// Read-only, never written by the server (it stays null in the document): what <c>coverage.yml</c> on the default branch says about the merge queue, as the dependency
    /// updater last read it (<c>enabled</c>, <c>disabled</c>, <c>absent</c>), or null when it has not read it yet (R26). Filled
    /// on load from the repository's dependency run, so the page shows the file's truth beside the onboarding setting.
    /// </summary>
    [IgnoreForIndex]
    public string? MergeQueueInCoverageYml { get; set; }

    /// <summary>R26: whether the onboarding pull request adds <c>mergeQueue: { enabled: true }</c>: the repository's choice,
    /// or its account's (Yes by default, and for a missing account).</summary>
    public static bool ResolveDependencyOnboardingMergeQueue(Repository? repository, Account? account)
        => repository?.DependencyOnboardingMergeQueue switch
        {
            EDependencyOnboardingMergeQueuePolicy.Yes => true,
            EDependencyOnboardingMergeQueuePolicy.No => false,
            _ => account?.DependencyOnboardingMergeQueue != EDependencyOnboardingMergeQueue.No,
        };

    /// <summary>Whether dependency updates run here: the repository's choice, or its account's. A missing account is off.</summary>
    /// <remarks>The file half (<c>dependencies.enabled: false</c>) is applied on top by the caller, and may only switch it off.</remarks>
    public static bool ResolveDependencyUpdates(Repository? repository, Account? account)
        => repository?.DependencyUpdates switch
        {
            EDependencyUpdatesPolicy.Enabled => true,
            EDependencyUpdatesPolicy.Disabled => false,
            _ => account?.DependencyUpdates ?? false,
        };

    /// <summary>The effective schedule and where it came from (<c>repository</c>, <c>organization</c> or <c>default</c>).</summary>
    /// <remarks>An hour outside 0–23 is read as the default hour; an unknown time zone as UTC.</remarks>
    public static (DependencySchedule Schedule, string Source) ResolveDependencySchedule(Repository? repository, Account? account)
    {
        var zone = account?.DependencyTimeZone is { Length: > 0 } id ? id : "UTC";
        static int Hour(int hour) => hour is >= 0 and <= 23 ? hour : CodeCoverage.Entities.Account.DefaultDependencyScheduleHour;
        if (repository?.DependencySchedulePolicy == EDependencySchedulePolicy.Override)
            return (new DependencySchedule(repository.DependencyScheduleFrequency, repository.DependencyScheduleWeekday, Hour(repository.DependencyScheduleHour), zone), "repository");
        return account is null
            ? (new DependencySchedule(EDependencyScheduleFrequency.Weekly, EDependencyWeekday.Monday, CodeCoverage.Entities.Account.DefaultDependencyScheduleHour, zone), "default")
            : (new DependencySchedule(account.DependencyScheduleFrequency, account.DependencyScheduleWeekday, Hour(account.DependencyScheduleHour), zone), "organization");
    }

    /// <summary>The effective cooldown and where it came from (Q16). A missing account resolves to Q16's three days.</summary>
    public static (TimeSpan Cooldown, string Source) ResolveDependencyCooldown(Repository? repository, Account? account)
        => repository?.DependencyCooldown switch
        {
            EDependencyCooldownPolicy.None => (TimeSpan.Zero, "repository"),
            EDependencyCooldownPolicy.OneDay => (TimeSpan.FromDays(1), "repository"),
            EDependencyCooldownPolicy.ThreeDays => (TimeSpan.FromDays(3), "repository"),
            EDependencyCooldownPolicy.SevenDays => (TimeSpan.FromDays(7), "repository"),
            _ => (account?.DependencyCooldown switch
            {
                EDependencyCooldown.None => TimeSpan.Zero,
                EDependencyCooldown.OneDay => TimeSpan.FromDays(1),
                EDependencyCooldown.SevenDays => TimeSpan.FromDays(7),
                _ => TimeSpan.FromDays(3),
            }, account is null ? "default" : "organization"),
        };

    /// <summary>The effective bisect budget in CI runs and where it came from (Q12). A missing account resolves to two.</summary>
    public static (int Runs, string Source) ResolveDependencyBisectBudget(Repository? repository, Account? account)
        => repository?.DependencyBisectBudget switch
        {
            EDependencyBisectBudgetPolicy.Off => (0, "repository"),
            EDependencyBisectBudgetPolicy.One => (1, "repository"),
            EDependencyBisectBudgetPolicy.Two => (2, "repository"),
            EDependencyBisectBudgetPolicy.Four => (4, "repository"),
            EDependencyBisectBudgetPolicy.Eight => (8, "repository"),
            _ => (account?.DependencyBisectBudget switch
            {
                EDependencyBisectBudget.Off => 0,
                EDependencyBisectBudget.One => 1,
                EDependencyBisectBudget.Four => 4,
                EDependencyBisectBudget.Eight => 8,
                _ => 2,
            }, account is null ? "default" : "organization"),
        };

    /// <summary>The effective check exclusions and where they came from (O12, O15); trimmed, distinct, ordinal-sorted.</summary>
    public static (IReadOnlyList<string> Exclusions, string Source) ResolveMergeQueueCheckExclusions(Repository? repository, Account? account)
    {
        var (list, source) = repository?.MergeQueueCheckExclusionsPolicy == EMergeQueueCheckExclusionsPolicy.Override
            ? (repository.MergeQueueCheckExclusions, "repository")
            : (account?.MergeQueueCheckExclusions ?? [], account is null ? "default" : "organization");
        return ([.. (list ?? []).Select(name => name?.Trim() ?? string.Empty).Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], source);
    }

    /// <summary>Whether the GitHub App can still see this repository.</summary>
    /// <remarks>
    /// Disconnected repositories keep every document they have and keep answering on their badge
    /// and report URLs; they stop appearing in listings for anyone who does not manage the owner.
    /// <para>
    /// Defaults to <see cref="RepositoryConnection.Connected"/>, which is what every document
    /// written before this field existed deserializes to — so no migration is needed and the
    /// nightly reconciler is what corrects the ones that are actually gone.
    /// </para>
    /// </remarks>
    public RepositoryConnection Connection { get; set; } = RepositoryConnection.Connected;

    /// <summary>Why the repository is disconnected, from <see cref="DisconnectedReasons"/>; null while connected.</summary>
    public string? DisconnectedReason { get; set; }

    /// <summary>When the repository was last disconnected (UTC); null while connected.</summary>
    public DateTime? DisconnectedAtUtc { get; set; }

    /// <summary>Names this repository was previously known by, oldest first.</summary>
    /// <remarks>
    /// Appended on every rename and transfer. Badge URLs live in READMEs and report links live in
    /// PR comments that were posted years ago, so a rename must not break them — these are what a
    /// stale <c>owner/name</c> resolves through.
    /// <para>
    /// A live <see cref="FullName"/> always wins over an alias, so a new repository taking over an
    /// old name shadows this list rather than colliding with it.
    /// </para>
    /// </remarks>
    public List<string> PreviousFullNames { get; set; } = [];

    /// <summary>Grants access to the rendered badge SVG only — never report data.</summary>
    /// <remarks>
    /// Set for private repositories; independently rotatable.
    /// <para>
    /// [IgnoreForIndex] because index membership is opt-out: without it this lands in VRepository,
    /// and synchronize then marks every projected field queryable — putting a live badge token in
    /// the /spark repository grid, which security.json grants to Everyone. Nothing filters or sorts
    /// on it, so the index has no use for it either.
    /// </para>
    /// </remarks>
    [IgnoreForIndex]
    public string? BadgeToken { get; set; }

    /// <summary>Gate policy; empty means every default (informational, auto-ratchet).</summary>
    /// <remarks>
    /// [IgnoreForIndex]: policy is owner-facing configuration — it has no business in the anonymous
    /// /spark grid, and nothing filters on it.
    /// </remarks>
    [IgnoreForIndex]
    public GateSettings? Gate { get; set; }

    /// <summary>The repository's headline coverage.</summary>
    /// <remarks>
    /// Denormalized from the newest finalized default-branch build, so repo lists and badges are
    /// point-loads.
    /// </remarks>
    public CoverageSummary? LatestCoverage { get; set; }

    /// <summary>The default-branch commit the headline coverage was taken from.</summary>
    public string? LatestCoverageSha { get; set; }

    /// <summary>When the headline coverage was last refreshed.</summary>
    /// <remarks>From the newest finalized default-branch build.</remarks>
    public DateTime? LatestCoverageAtUtc { get; set; }

    /// <summary>
    /// How many fork-contributed uploads this repository has accepted in the current window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>A budget of its own, deliberately separate from anything the owner spends.</b> The
    /// anonymous fork endpoint has no caller identity to charge — that is what makes it anonymous —
    /// so the only thing a quota can be attached to is the target repository. Charging fork uploads
    /// to the <em>same</em> budget as the repository's own would convert a storage problem into an
    /// availability one: a stranger opening pull requests could exhaust a repository's allowance and
    /// get its owner's CI refused. <see cref="Commit.ContributedFromFork"/> already separates the
    /// two populations, so the budget can too.
    /// </para>
    /// <para>
    /// ⚠️ <b>Documents are what is metered, not bytes.</b> Report attachments are already bounded —
    /// the reaper deletes them after <c>Coverage:Retention:ReportAttachmentDays</c> — but the
    /// documents they expand into are permanent: one <c>FileCoverage</c> per file, plus one per file
    /// <em>per flag</em>, plus an assembled copy. And <b>nothing reaps a fork pull request that is
    /// closed without merging, or simply left open</b>; only a merge triggers
    /// <c>DeletePullRequestBuildsRecipient</c>. So the recoverable half is bounded and the permanent
    /// half was not.
    /// </para>
    /// <para>
    /// Null on every repository that has never taken one, which is almost all of them — the field
    /// costs nothing until it is used.
    /// </para>
    /// </remarks>
    public ForkUploadBudget? ForkUploads { get; set; }

    /// <summary><c>Repositories/{provider}/{repositoryId}</c>.</summary>
    /// <remarks>
    /// The provider segment is required because a numeric repository id is only unique
    /// <em>within</em> a forge (D25, and test A2 pins exactly this collision).
    /// </remarks>
    public static string DocumentId(EForgeProvider provider, long repositoryId)
        => $"Repositories/{provider.ToCanonicalString()}/{repositoryId}";
}
