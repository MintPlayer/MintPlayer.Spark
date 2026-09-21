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
    /// <remarks>Stable across renames and transfers.</remarks>
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
        var previous = FullName;
        if (string.IsNullOrEmpty(previous) || previous == newFullName) return;
        if (PreviousFullNames.Contains(previous, StringComparer.OrdinalIgnoreCase)) return;

        PreviousFullNames.Add(previous);
        if (PreviousFullNames.Count > MaxPreviousFullNames)
            PreviousFullNames.RemoveAt(0);
    }

    public static bool ResolveDeleteBranchOnPrClose(Repository? repository, Account? account)
        => repository?.DeleteBranchOnPrClose switch
        {
            EDeleteBranchPolicy.Enabled => true,
            EDeleteBranchPolicy.Disabled => false,
            _ => account?.DeleteBranchOnPrClose ?? false,
        };

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
