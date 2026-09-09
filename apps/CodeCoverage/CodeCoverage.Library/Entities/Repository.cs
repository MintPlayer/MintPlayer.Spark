using CodeCoverage.LookupReferences;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// A GitHub repository the app knows about (installed via the GitHub App).
/// Document id is Repositories/{GitHubId} so webhook upserts are idempotent.
/// </summary>
[GenerateIndex]
public class Repository
{
    /// <summary>Document id of this repository, <c>Repositories/{GitHubId}</c>.</summary>
    public string? Id { get; set; }

    /// <summary>The GitHub user or organization that owns this repository.</summary>
    [Reference(typeof(Account))]
    public string? Account { get; set; }

    /// <summary>GitHub's numeric id for this repository.</summary>
    /// <remarks>Stable across renames and transfers.</remarks>
    public long GitHubId { get; set; }

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

    public static string DocumentId(long gitHubId) => $"Repositories/{gitHubId}";
}
