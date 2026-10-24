using CodeCoverage.Entities;

namespace CodeCoverage.Forge;

/// <summary>
/// Everything the app needs from one forge, behind one interface (D16).
/// </summary>
/// <remarks>
/// <para>
/// <b>Callers never name a forge.</b> Every consumer injects
/// <see cref="IEnumerable{T}"/> of this type and selects through
/// <see cref="IForgeIntegrationResolver"/>; no call site contains <c>if (github)</c>. This is the
/// property the whole abstraction exists for — adding GitLab must be writing an implementation, not
/// editing consumers.
/// </para>
/// <para>
/// <b>There are two call shapes and they resolve differently.</b> <em>Fan-out</em> operations
/// (<see cref="GetAllowedOwnersAsync"/>) ask every registered integration and concatenate, so the
/// caller never learns how many forges exist. <em>Select-one</em> operations — everything taking a
/// <see cref="Repository"/> — can only be answered by the one integration that owns it, chosen from
/// the document, never by the caller. Conflating the two is the easiest way to get this wrong.
/// </para>
/// <para>
/// <b>No credential appears in any signature.</b> Resolving one is the implementation's business:
/// a GitHub App installation, a GitLab group token and a Bitbucket workspace credential have
/// nothing in common, and a neutral caller holding one would be an abstraction in name only. This
/// is the single rule that decides whether this interface is real.
/// </para>
/// <para>
/// <b>Capabilities are declared, not discovered by calling</b> (D17). The forges are not
/// feature-equivalent — GitHub has Projects V2 boards, Bitbucket has no boards at all — so members
/// outside an implementation's <see cref="Capabilities"/> throw
/// <see cref="NotSupportedException"/>, and callers filter on the array first. A conformance test
/// asserts the array and the behaviour agree in both directions, because nothing else can: this is
/// a runtime contract with no compiler behind it.
/// </para>
/// </remarks>
public interface IForgeIntegration
{
    /// <summary>The single forge this instance speaks for.</summary>
    EForgeProvider Provider { get; }

    /// <summary>
    /// What this forge supports. Members belonging to a capability not listed here throw
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    /// <remarks>
    /// Get-only and constant per implementation: a capability that varies per request or per
    /// account is not a capability, it is an availability question, and belongs in
    /// <see cref="CheckAccessAsync"/> instead.
    /// </remarks>
    EForgeCapability[] Capabilities { get; }

    // ── Identity and authorization ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The owners the current viewer may see on this forge, plus the health of their credential.
    /// Empty (and <see cref="EForgeCredentialState.Ok"/>) for an anonymous viewer; degraded to the
    /// viewer's own account when the forge cannot be reached.
    /// </summary>
    /// <remarks>
    /// <b>The forge is the authority.</b> No app-local ACL, no stored grant — the answer is derived
    /// live on each call behind a cache, so revoking access at the forge revokes it here. An
    /// implementation must never consult a stored record as its source of truth, and must never
    /// return owners belonging to another forge.
    /// <para>
    /// <b>Degradation is part of the contract.</b> <em>Failure is not absence</em>: a degraded
    /// answer must never be cached as a successful one, and every degraded return MUST be logged,
    /// because the user-visible symptom is an account list that silently loses rows.
    /// </para>
    /// <para>
    /// <b>Failures must be cached briefly.</b> Caching only successes is defensible with one forge
    /// and dangerous with three — Bitbucket's budget of roughly 1,000 requests per hour per token
    /// can be exhausted by failures alone and then stay exhausted.
    /// </para>
    /// </remarks>
    Task<ForgeVisibility> GetVisibilityAsync(CancellationToken cancellationToken = default);

    /// <summary>The owner set alone, for callers that do not care why it is what it is.</summary>
    Task<ForgeOwner[]> GetAllowedOwnersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the viewer may SEE this owner's private repositories: membership of the owner set, nothing more. ⚠️ Not a
    /// management right (dependency-updates PRD D-O12): who manages an owner is <see cref="ManagesOwnerAsync"/>.
    /// </summary>
    Task<bool> IsOwnerAllowedAsync(ForgeOwner owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the viewer MANAGES this owner (R39, D-O12): their own personal account, or an organization of which the forge
    /// says they are an active owner. Read/write members are not managers. See <see cref="Services.IForgeAccessService.ManagesOwnerAsync"/>.
    /// </summary>
    Task<bool> ManagesOwnerAsync(ForgeOwner owner, CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the viewer's cached owner set for this forge so the next call re-queries it (the
    /// manual resync). Must not disturb any other forge's cache entry.
    /// </summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);

    // ── Reads ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether we hold a credential that can act for this repository, and if not, a message fit to
    /// show a user.
    /// </summary>
    /// <remarks>
    /// Separate from the reads because "no credential" and "the read found nothing" are different
    /// states deserving different handling — the feedback pipeline parks a build as unavailable for
    /// the first and retries the second. The message comes from the implementation so it can name
    /// the real remedy without the caller knowing which forge it is talking to.
    /// </remarks>
    Task<ForgeAccess> CheckAccessAsync(Repository repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// Three-dot compare (<c>base...head</c>): the merge-base commit, and per changed file the
    /// lines the head side added in new-file numbering. Null when no path to the forge exists.
    /// </summary>
    Task<CommitComparison?> CompareAsync(Repository repository, string baseRef, string headSha, CancellationToken cancellationToken = default);

    /// <summary>
    /// The git first parent of <paramref name="sha"/> according to the forge, or null when no API
    /// path exists or the call fails. Authoritative over any stored hint.
    /// </summary>
    Task<string?> GetFirstParentAsync(Repository repository, string sha, CancellationToken cancellationToken = default);

    /// <summary>Source of one file at an exact commit, or null when absent (a 404). Never stored. Any other failure throws: see <see cref="IForgeClient.GetFileContentAsync"/>.</summary>
    Task<string?> GetFileContentAsync(Repository repository, string sha, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every file in the head commit of <paramref name="branch"/>, or null when there is none (a 404); any other failure throws.
    /// See <see cref="IForgeClient.GetTreeAsync"/>.
    /// </summary>
    /// <remarks>
    /// No capability guards this: every forge can list a repository tree (GitHub's git trees, GitLab's
    /// repository tree, Bitbucket's src listing), so refusing would be an availability answer rather
    /// than a capability one — the same reasoning as <see cref="DeleteBranchAsync"/>. The conformance
    /// test holds every implementation to never refusing it.
    /// </remarks>
    Task<ForgeTree?> GetTreeAsync(Repository repository, string branch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one pull request on <paramref name="repository"/>, or null when it cannot be read.
    /// See <see cref="IForgeClient.GetPullRequestAsync"/> — null is a refusal, never an absence.
    /// </summary>
    Task<ForgePullRequest?> GetPullRequestAsync(Repository repository, int number, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-derives what this forge says an account owns, correcting whatever drifted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Mutates documents in the caller's session and does NOT save.</b> The caller owns the
    /// unit of work — a nightly sweep saves once for the whole pass, a single-account reconcile
    /// saves immediately, and an implementation that saved for itself would take that choice away
    /// and turn one failure into a half-applied sweep.
    /// </para>
    /// <para>
    /// ⚠️ <b>Fail closed.</b> A transient error must leave everything as it was rather than
    /// disconnecting what could not be listed — an outage is not the same fact as "the owner
    /// revoked us", and treating it as one un-advertises every repository until the next pass.
    /// </para>
    /// <para>
    /// This exists so the app can sweep <em>every</em> linked forge without naming one. The
    /// scheduler asks each integration in turn; what "listing an account's repositories" means is
    /// the implementation's business.
    /// </para>
    /// </remarks>
    Task ReconcileAsync(Account account, CancellationToken cancellationToken = default);

    // ── Git access (commit graph, #504) ──────────────────────────────────────────────────────────

    /// <summary>
    /// How a <c>git</c> process may clone or fetch <paramref name="repository"/>: the https clone
    /// URL and, when one is needed, a credential minted for this one call. Null when there is no
    /// path at all (a private repository with no credential behind it).
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>The credential is never stored.</b> It is handed to the mirror sidecar in the body of
    /// one request and dropped; the sidecar does not persist it either (PRD §4.1, D3). It is asked
    /// for once per fetch, but an implementation may serve it from a shared token cache (GitHub's
    /// installation tokens are cached for their hour); it must then guarantee the token outlives the
    /// fetch quota, as GitHub's does by asking the cache for at least 15 minutes of remaining life.
    /// </para>
    /// <para>
    /// An anonymous answer (<see cref="ForgeGitAccess.IsAnonymous"/>) is for a public repository
    /// with no credential (D6). The caller applies the stricter anonymous quota to it.
    /// </para>
    /// </remarks>
    Task<ForgeGitAccess?> GetGitAccessAsync(Repository repository, CancellationToken cancellationToken = default);

    // ── Writes ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates or updates one named status on a commit, returning the forge's id for it so a
    /// re-publish updates rather than duplicates.
    /// </summary>
    /// <param name="existingId">The id from a previous publish, or null to create.</param>
    Task<long> PublishStatusAsync(
        Repository repository,
        string sha,
        string name,
        ForgeVerdict verdict,
        long? existingId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a branch. Best-effort and never throws.
    /// </summary>
    /// <remarks>
    /// No capability guards this: every forge can delete a ref, so refusing would be an
    /// availability answer rather than a capability one.
    /// <para>
    /// <b>Never throws, deliberately.</b> This runs after a merge has already landed — the branch is
    /// a courtesy, and failing the caller over it would cost the event and change nothing about the
    /// merge. Implementations log their own outcomes, because the interesting cases are
    /// forge-specific: a GitHub App missing an accepted <c>contents: write</c> looks identical to a
    /// transient fault unless it is named.
    /// </para>
    /// <para>
    /// ⚠️ The caller decides <em>whether</em> to delete. An implementation must not consult policy,
    /// and must not refuse a fork's branch on its own — by the time this is called the decision has
    /// been taken with information the implementation does not have.
    /// </para>
    /// </remarks>
    Task DeleteBranchAsync(Repository repository, string branch, CancellationToken cancellationToken = default);

    /// <summary>Ensures the pull request carries exactly one comment with this body.</summary>
    /// <remarks>
    /// Never throws: every outcome is recorded on the feedback outbox, because a failure here must
    /// not undo statuses already published in the same invocation.
    /// </remarks>
    Task PublishCommentAsync(
        Repository repository,
        int pullRequestNumber,
        string sha,
        string body,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a forge supports (D17). A member belonging to a capability an implementation does not
/// declare throws <see cref="NotSupportedException"/>.
/// </summary>
/// <remarks>
/// Deliberately coarse. A capability is worth a member here only when a caller would otherwise have
/// to ask "which forge is this?" to know whether to call — which is exactly the question this
/// interface exists to remove.
/// </remarks>
public enum EForgeCapability
{
    /// <summary>
    /// Commit statuses / check runs. Every forge has some form, but only GitHub has a first-class
    /// neutral conclusion — see <see cref="EForgeOutcome.Neutral"/>.
    /// </summary>
    Statuses,

    /// <summary>Pull-request comments. Bitbucket prefers Code Insights reports; the mapping is per forge.</summary>
    Comments,

    /// <summary>
    /// Project boards. GitHub Projects V2 only: GitLab's issue boards do not map and Bitbucket has
    /// none. The canonical example of why this enum exists.
    /// </summary>
    Boards,

    /// <summary>
    /// CI job tokens usable as an upload credential. ⚠️ Present on GitHub for non-fork runs only —
    /// GitHub never mints one for a pull request from a fork, which is what D6f is about.
    /// </summary>
    CiIdentity,

    /// <summary>
    /// A git clone URL and a credential for the commit-graph mirror
    /// (<see cref="IForgeIntegration.GetGitAccessAsync"/>, #504). GitLab (<c>oauth2</c>) and
    /// Bitbucket (<c>x-token-auth</c>) declare it once they hold a credential to hand out.
    /// </summary>
    GitMirror,

    /// <summary>
    /// The conflict-resolving merge queue (merge-queue PRD M4). Its members live on the separate
    /// <see cref="IForgeMergeQueue"/>; a forge declaring this registers exactly one implementation of it
    /// for its provider, and the conformance test holds both directions. GitHub only for now (O7).
    /// </summary>
    MergeQueue,

    /// <summary>
    /// Buttons on the App's own check that report a click back to the App: GitHub's check-run <em>requested
    /// actions</em>, which carry the merge queue's "Add to merge queue" (merge-queue PRD §0 O30). GitHub only:
    /// GitLab's commit statuses and Bitbucket's Code Insights reports have no buttons. Its one member is
    /// <see cref="IForgeMergeQueue.PublishQueueCheckAsync"/> with <see cref="ForgeQueueCheck.Actions"/>, so declaring
    /// it takes <see cref="MergeQueue"/> too; a merge queue whose forge does not declare it refuses a check with actions.
    /// </summary>
    CheckButtons,

    /// <summary>
    /// The repositories of the App's installations a signed-in viewer can reach, with whether the viewer administers each,
    /// and the same checked again with the App's credential when an action executes (dependency-updates PRD R21, R32, R33).
    /// Its members live on <see cref="IForgeRepositoryAdministration"/>. GitHub only for now.
    /// </summary>
    RepositoryAdminCheck,

    /// <summary>
    /// The forge sends the viewer back to the app after installing it, with parameters <see cref="IForgeSetupRedirect"/>
    /// reads (R20, R36). GitHub's App setup URL; GitLab or Bitbucket would need an OAuth app or a Connect descriptor.
    /// </summary>
    SetupRedirect,

    /// <summary>
    /// Label definitions on a repository, and labels and changed files on a pull request, for the PR size labeller
    /// (docs/coverage_pr_size_labels_PRD.md D8). Its members live on <see cref="IForgeLabels"/>. GitHub only: Bitbucket
    /// Cloud has no pull-request labels, and GitLab has no forge here yet (S4).
    /// </summary>
    PullRequestLabels,

    /// <summary>
    /// Pull-request titles, Releases, release branches, backport pull requests and workflow dispatch for package versioning
    /// (docs/coverage_package_versioning_PRD.md D16). Its members live on <see cref="IForgeVersioning"/>. GitHub only; the
    /// settings are hidden for a forge without it.
    /// </summary>
    Versioning,
}

/// <summary>How a git process reaches one repository (<see cref="IForgeIntegration.GetGitAccessAsync"/>).</summary>
/// <param name="CloneUrl">The https clone URL. Never carries a credential.</param>
/// <param name="Username">The credential's user name (<c>x-access-token</c> on GitHub), or null when anonymous.</param>
/// <param name="Password">The credential itself, or null when anonymous. ⚠️ Never log or store it.</param>
/// <param name="InstallationKey">
/// What the credential belongs to (on GitHub, the App installation), so the mirror budget can be
/// capped per installation (D10). Null when anonymous. Opaque to the caller.
/// </param>
public sealed record ForgeGitAccess(string CloneUrl, string? Username, string? Password, string? InstallationKey)
{
    /// <summary>A public repository cloned without a credential (D6).</summary>
    public bool IsAnonymous => Password is null;

    /// <summary>Anonymous access to a public repository.</summary>
    public static ForgeGitAccess Anonymous(string cloneUrl) => new(cloneUrl, null, null, null);

    /// <summary>Redacted: a record prints every member, and this one carries a credential.</summary>
    public override string ToString()
        => $"ForgeGitAccess {{ CloneUrl = {CloneUrl}, Anonymous = {IsAnonymous}, InstallationKey = {InstallationKey} }}";
}

/// <summary>What the viewer may see on one forge, and whether the answer can be trusted as complete.</summary>
/// <param name="Owners">The accounts the viewer may see. Degraded answers carry only their own.</param>
/// <param name="State">Why the set is what it is.</param>
public sealed record ForgeVisibility(ForgeOwner[] Owners, EForgeCredentialState State);

/// <summary>The health of the viewer's credential for one forge.</summary>
/// <remarks>
/// The distinction between <see cref="Unavailable"/> and an empty <see cref="Ok"/> is load-bearing:
/// one means "we could not find out", the other "we asked and the answer is none". Collapsing them
/// makes an outage indistinguishable from having no accounts — a confusion this product has already
/// been bitten by.
/// </remarks>
public enum EForgeCredentialState
{
    /// <summary>The forge answered. The owner set is complete.</summary>
    Ok,

    /// <summary>The credential is dead and only a browser round trip can fix it.</summary>
    ReauthRequired,

    /// <summary>The forge could not be reached. The owner set is degraded, not empty.</summary>
    Unavailable,
}

/// <summary>Whether the forge can be acted upon for a repository.</summary>
/// <param name="Available">True when a usable credential was resolved.</param>
/// <param name="UnavailableReason">
/// Implementation-supplied, user-facing explanation when <paramref name="Available"/> is false.
/// Null when available. Supplied by the implementation precisely so neutral callers never name a forge.
/// </param>
public sealed record ForgeAccess(bool Available, string? UnavailableReason)
{
    /// <summary>A usable credential was resolved.</summary>
    public static ForgeAccess Yes { get; } = new(true, null);

    /// <summary>No usable credential, with a reason fit to show a user.</summary>
    public static ForgeAccess No(string reason) => new(false, reason);
}

/// <summary>
/// Thrown when the forge refuses an action we believed we were entitled to perform — a revoked
/// grant, a suspended installation, a removed credential.
/// </summary>
/// <remarks>
/// Exists so neutral callers can catch <em>one</em> type instead of each forge's own exception
/// (<c>Octokit.ApiException</c> and a status-code check, in the GitHub case). A second forge would
/// have had nothing equivalent to catch.
/// </remarks>
/// <param name="requiredPermission">
/// The permission the forge says the call needed, when it says (GitHub's
/// <c>X-Accepted-GitHub-Permissions</c>, e.g. <c>actions=write</c>), so the caller can point at the
/// acceptance page (merge-queue O17). Null when unknown.
/// </param>
public sealed class ForgeAccessDeniedException(string message, string? requiredPermission = null) : Exception(message)
{
    public string? RequiredPermission { get; } = requiredPermission;
}

/// <summary>What we are telling the forge about a commit.</summary>
/// <param name="Outcome">The conclusion, in our vocabulary rather than any forge's.</param>
/// <param name="Title">Short headline.</param>
/// <param name="Summary">
/// One or two lines. ⚠️ Keep it short: GitLab caps a status description at 255 characters and
/// Bitbucket's Code Insights allows ten data cells, so detail belongs in the comment, not here.
/// </param>
public sealed record ForgeVerdict(EForgeOutcome Outcome, string Title, string Summary);

/// <summary>
/// The conclusion vocabulary, declared here rather than passing a forge's enum through.
/// </summary>
/// <remarks>
/// GitHub concludes success / failure / neutral (and more); GitLab has pending, running, success,
/// failed, canceled, skipped; Bitbucket has SUCCESSFUL, FAILED, INPROGRESS, STOPPED. Only GitHub has
/// a true neutral, so the mapping is stated per implementation and lost deliberately rather than by
/// accident.
/// </remarks>
public enum EForgeOutcome
{
    /// <summary>The gate passed.</summary>
    Success,

    /// <summary>The gate failed.</summary>
    Failure,

    /// <summary>
    /// No judgement — coverage could not be computed, or must not gate this build. ⚠️ Only GitHub
    /// has a first-class neutral; elsewhere this maps to the closest non-failing state. D20 makes
    /// this the outcome for an upload that could not happen, because an error would be intrusive.
    /// </summary>
    Neutral,
}
