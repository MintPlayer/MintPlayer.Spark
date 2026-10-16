namespace CodeCoverage.Forge;

/// <summary>
/// The events the app reacts to, in its own vocabulary rather than any forge's (D21).
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from what the app does, not from what GitHub sends.</b> That distinction is the whole
/// design: enumerating a forge's event names and calling them canonical produces a vocabulary that
/// fits exactly one forge and has to be bent for the next. Each type here exists because some part
/// of this app takes a decision on it — a commit is recorded, builds are deleted, a name is
/// rewritten, a connection changes.
/// </para>
/// <para>
/// <b>An event only belongs here if all three forges can raise it.</b> Anything one forge alone can
/// say — GitHub's <c>check_run</c>, Projects V2 — keeps a forge-specific message and a recipient
/// that is honestly forge-specific. A neutral name over a single-forge concept is worse than an
/// honest one, because it invites an implementation that cannot exist (D17's reasoning, applied to
/// messages).
/// </para>
/// </remarks>
public static class ForgeEvents
{
    // Nothing here. The class is a documentation anchor for the records below, which live in this
    // file because they are one vocabulary and reading them apart loses the point.
}

/// <summary>
/// A fact about a repository, pull request or owner, in this app's vocabulary rather than any
/// forge's.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Implementing this is a commitment, not a label.</b> An event in this vocabulary must have a
/// producer in every forge library and a consumer in the app — <c>ForgeEventContractTests</c>
/// asserts both, because a declared event with neither is worse than a missing one: a gap is
/// obvious, while a promise the next person reasonably believes is not.
/// </para>
/// <para>
/// It also makes the set discoverable. Somebody writing the GitLab normaliser needs to know what
/// they are expected to raise, and "every record in this file" is not something a compiler or a
/// reader can check.
/// </para>
/// </remarks>
public interface IForgeEvent;

/// <summary>A commit arrived on a branch.</summary>
/// <param name="RepositoryId">The repository document this concerns.</param>
/// <param name="Branch">Branch name with any refs/heads/ prefix already stripped — that shape is GitHub's wire format, not ours.</param>
/// <param name="Sha">The new branch head.</param>
/// <param name="Message">Commit message, when the forge sends one.</param>
/// <param name="AuthoredAt">When the commit was authored, when the forge sends it.</param>
/// <remarks>
/// ⚠️ <b>Deliberately carries no parent sha</b>, and that is not an oversight. GitHub's <c>before</c>
/// is the previous ref tip, which is not the new commit's parent in three of the six push shapes: a
/// push of five commits reports the tip five commits back, a branch creation reports the all-zero
/// sha, and a force-push reports an abandoned tip that need not be an ancestor at all. Any forge's
/// equivalent field has the same problem, so the neutral event refuses to carry a value it cannot
/// define. The pull-request event is the one writer of a parent, and means something precise by it.
/// </remarks>
public sealed record BranchCommitPushed(
    string RepositoryId,
    string Branch,
    string Sha,
    string? Message,
    DateTimeOffset? AuthoredAt) : IForgeEvent;

/// <summary>
/// A pull request was opened, reopened, had new commits pushed to it, was marked ready for review, or was retargeted.
/// </summary>
/// <param name="IsFirstOpen">
/// True for an open or reopen, false for a later push. The only consumer that cares is the pending
/// coverage comment, which must be posted once rather than on every push — and a forge that cannot
/// distinguish the two should report false, because a duplicate comment is worse than a missing one.
/// </param>
/// <param name="AuthorIsBot">
/// A pull request no human opened gets no "waiting for coverage" comment, because nothing will ever
/// arrive to replace it. Forges that cannot tell report false.
/// </param>
/// <remarks>
/// The three GitHub actions <c>opened</c>, <c>reopened</c> and <c>synchronize</c> collapse to this
/// one event because the app does the same thing for all three: re-record the head commit and its
/// base. Keeping them apart would export a distinction no consumer uses.
/// <para>
/// <c>ready_for_review</c> and an <c>edited</c> that changed the base join them (merge-queue PRD §0 O30): the
/// pull request may have just become one the merge queue can take, so the queue offers its button again, and
/// a retarget moves the base the coverage record keeps. A title or body edit raises nothing.
/// </para>
/// </remarks>
public sealed record PullRequestUpdated(
    string RepositoryId,
    int Number,
    string HeadSha,
    string HeadRef,
    string BaseRef,
    string BaseSha,
    string? Title,
    bool IsFirstOpen,
    bool AuthorIsBot) : IForgeEvent;

/// <summary>A pull request was merged.</summary>
/// <param name="HeadRef">The source branch, for the optional delete-after-merge courtesy.</param>
/// <remarks>
/// ⚠️ <b>Merged, not closed.</b> The two are not interchangeable: retention surrenders a merged pull
/// request's build data, while a closed-unmerged one keeps it, because it may reopen and nothing
/// about it is final. A forge that reports only "closed" must determine mergedness before raising
/// this, and raise nothing if it cannot — deleting builds on a guess is unrecoverable.
/// </remarks>
/// <param name="HeadIsFromSameRepository">
/// False when the request came from a fork. ⚠️ Load-bearing for the delete-after-merge courtesy: a
/// fork's head branch lives in a repository we were never given write access to and whose owner
/// still wants it, and the opt-in lives on the <em>base</em> repository, which cannot speak for it.
/// A forge that cannot tell must report false — declining to delete is recoverable, deleting
/// someone else's branch is not.
/// </param>
/// <param name="MergeCommitSha">
/// The commit the merge created on the base branch, when the forge's event names it (GitHub's
/// <c>merge_commit_sha</c>). The merge queue records it when this event, rather than its own poll,
/// completes a merge it requested (merge-queue PRD §0 D94); null when the forge does not say.
/// </param>
public sealed record PullRequestMerged(
    string RepositoryId,
    int Number,
    string? HeadRef,
    bool HeadIsFromSameRepository,
    string? MergeCommitSha = null) : IForgeEvent;

/// <summary>A repository changed its name or moved to a different owner.</summary>
/// <remarks>
/// The stored document keeps its id — the forge's numeric id is stable across a rename, which is
/// exactly why ids are keyed on it rather than on the name. ⚠️ Bitbucket slugs are renameable
/// <em>and reusable</em>, so a Bitbucket implementation must not treat a name as an identity.
/// </remarks>
/// <param name="PreviousFullName">
/// The <c>owner/name</c> we knew it by until now, or null when the forge cannot say.
/// <para>
/// ⚠️ <b>Carried on the event rather than read from the document, because by the time a consumer
/// runs the document may already say the new name.</b> A forge library that upserts repository
/// metadata from the same payload will have overwritten it, and then the alias appended would be
/// the name it already has — which is not an alias, it is a no-op that looks like one. The value a
/// consumer needs is the one only the producer still knows.
/// </para>
/// </param>
public sealed record RepositoryRenamed(
    string RepositoryId,
    string NewName,
    string NewFullName,
    string NewOwnerLogin,
    string? PreviousFullName) : IForgeEvent;

/// <summary>An account (user or organisation) changed its login.</summary>
/// <param name="NewAvatarUrl">
/// The account's avatar as of the rename, when the forge sends one. Carried here rather than on an
/// event of its own because every forge reports it alongside the rename, and a separate
/// "avatar changed" event would have no consumer.
/// </param>
/// <remarks>
/// ⚠️ The account keeps its numeric id, so the document is the same one — but its login, and the
/// owner half of every full name beneath it, are now wrong. Renaming an owner is therefore not a
/// one-document change; it rewrites every repository under it.
/// </remarks>
public sealed record OwnerRenamed(
    string AccountId,
    string NewLogin,
    string? NewAvatarUrl) : IForgeEvent;

/// <summary>
/// Whether we can still act on a repository changed.
/// </summary>
/// <param name="Connected">True when access was granted or restored, false when it was lost.</param>
/// <param name="Reason">
/// Why, in terms a user could read. Stored on the repository, so it survives to explain a disconnected
/// row long after the event.
/// </param>
/// <remarks>
/// <b>This is where GitHub's installation events land, and why they do not need a neutral name of
/// their own.</b> An installation is GitHub's <em>mechanism</em> for granting access; the domain
/// fact is that a repository became reachable or stopped being so. GitLab would raise this from a
/// revoked group token, Bitbucket from an uninstalled app — different mechanisms, same fact. Naming
/// the event after the mechanism would have made it GitHub-only for no reason (D15).
/// </remarks>
/// <param name="ReportedByAccountId">
/// The account whose access changed, when the forge can say which one — or null when the fact is
/// about the repository itself rather than about somebody's access to it (a deletion, say).
/// <para>
/// ⚠️ <b>Load-bearing for a lost-access event, and it settles an ordering hazard without needing an
/// order.</b> When one repository moves between two owners we both have access to, three events
/// describe the move and arrive in no guaranteed sequence: the old owner reports losing it, the new
/// owner reports gaining it. A consumer that acts on the loss unconditionally would disconnect a
/// repository it can plainly still see, and leave it that way until the next reconcile.
/// </para>
/// <para>
/// With this, the consumer compares: if the repository has already been re-parented, the loss is
/// the <em>old</em> owner reporting something that is no longer theirs, and it is stale. If it has
/// not, the loss is current. Correct whichever way round the two arrive — which is the only way to
/// be correct, because there is no order to rely on.
/// </para>
/// </param>
public sealed record RepositoryConnectionChanged(
    string RepositoryId,
    bool Connected,
    string? Reason,
    string? ReportedByAccountId = null) : IForgeEvent;

// ── The merge queue (merge-queue PRD M5) ──────────────────────────────────────────────────────────────
//
// Every forge can raise these: GitLab and Bitbucket have labels or comments, pipelines and commit statuses
// too. Only the GitHub normaliser raises them today (O7: GitHub only in this PR; the others under #422).

/// <summary>
/// Somebody asked the merge queue to take a pull request, or to let it go (O8).
/// </summary>
/// <param name="ActorLogin">
/// ⚠️ <b>The authenticated actor, never text from the request (T1).</b> For a label, a comment or a click on the queue
/// check's button that is the signed webhook's sender; for the repository page's button, the signed-in user's linked
/// forge login. A comment saying "/queue as @maintainer" is still the commenter's, and a click on the App's own check is
/// the clicker's. A normaliser that cannot name the actor must raise nothing.
/// </param>
/// <param name="Label">The label added or removed, for <see cref="EMergeQueueCommandSource.Label"/>: the queue compares it
/// with the configured one, which the normaliser does not know.</param>
/// <remarks>
/// A normaliser drops commands a bot sent, the App's own label removals included: the queue only ever acts for a
/// person (§0 D46).
/// </remarks>
public sealed record MergeQueueCommandIssued(
    string RepositoryId,
    int Number,
    EMergeQueueCommand Command,
    string ActorLogin,
    Entities.EMergeQueueCommandSource Source,
    string? Label = null) : IForgeEvent;

public enum EMergeQueueCommand
{
    Enqueue,
    Dequeue,
}

/// <summary>A pull request was closed without being merged.</summary>
/// <remarks>The merged close is <see cref="PullRequestMerged"/>; the two are never raised for the same close.</remarks>
public sealed record PullRequestClosed(string RepositoryId, int Number) : IForgeEvent;

/// <summary>A CI run (a GitHub Actions workflow run, a GitLab pipeline) finished.</summary>
/// <param name="HeadSha">The commit the run built, which the queue compares with the commit it asked for (D14).</param>
/// <param name="Conclusion">The forge's word; the queue re-reads the run before acting on it.</param>
public sealed record CiRunCompleted(string RepositoryId, long RunId, string HeadSha, string? Conclusion) : IForgeEvent;

/// <summary>A check or status on a commit was created or changed state.</summary>
/// <remarks>Carries no verdict: the queue re-reads every check on the commit and decides with <see cref="CheckGate"/>.</remarks>
public sealed record CommitChecksChanged(string RepositoryId, string Sha) : IForgeEvent;

/// <summary>A review was submitted on, or dismissed from, a pull request.</summary>
public sealed record PullRequestReviewed(string RepositoryId, int Number) : IForgeEvent;

/// <summary>
/// A check run or commit status reported on a commit of the repository, whatever its outcome (merge-queue PRD §11, D108):
/// one of the names the merge queue's exclusion lists may pick.
/// </summary>
/// <param name="Kind">Null when the source had the name alone.</param>
/// <param name="AppSlug">The App that made the check run, when the forge says so.</param>
/// <remarks>
/// Raised whether or not the queue is busy, unlike the signals above, but only for a name that is new or a day stale
/// (<see cref="Entities.ForgeCheck.Due"/>), so a repository is not rewritten on every delivery. The App's own check runs
/// are not raised.
/// </remarks>
public sealed record CheckReported(string RepositoryId, string Name, Entities.EForgeCheckKind? Kind, string? AppSlug) : IForgeEvent;

// ── The dependency updater (dependency-updates PRD §7.5, §7.6, R10) ────────────────────────────────────────────────
//
// Every forge has comments, releases (or tags) and an advisory feed; only the GitHub normaliser raises these today.

/// <summary>
/// A person wrote one of the dependency updater's comment commands (§7.5, R6) as the first word of a NEW comment on a pull
/// request: <c>approve</c>, <c>hold</c>, <c>unhold</c>, <c>bisect</c> or <c>refresh</c>.
/// </summary>
/// <param name="ActorLogin">The signed webhook's sender (T1), never text from the comment. A normaliser drops edited comments,
/// comments a bot or the App itself wrote, and raises nothing when it cannot name the actor (gap E).</param>
/// <param name="Argument">The rest of the first line (a group or package name), trimmed; null when there is none.</param>
/// <remarks>Whether the pull request is the bot's, and whether the actor may command it, is the saga's to decide (the
/// role is read from the forge per command, R6).</remarks>
public sealed record DependencyCommandIssued(
    string RepositoryId,
    int Number,
    long CommentId,
    EDependencyCommand Command,
    string? Argument,
    string ActorLogin) : IForgeEvent;

public enum EDependencyCommand
{
    Approve,
    Hold,
    Unhold,
    Bisect,
    Refresh,
}

/// <summary>
/// A repository published a release (§7.6, gap E): one of the three facts an "own package" needs, the others being that the
/// repository is on the same account and declares the package. <paramref name="PublishedAt"/> is the forge's time, compared
/// with the registry's publish time the lookup reports.
/// </summary>
public sealed record ReleasePublished(string RepositoryId, string Tag, DateTimeOffset PublishedAt) : IForgeEvent;

/// <summary>
/// The forge's advisory feed named a vulnerable dependency of the repository (R10): it skips the schedule and the cooldown.
/// </summary>
/// <param name="Ecosystem">The ecosystem as the lookup records name it (<c>npm</c>, <c>nuget</c>, <c>pypi</c>, …), mapped by
/// the normaliser from the forge's own word.</param>
public sealed record DependencyAlertRaised(string RepositoryId, string Ecosystem, string DepName, string? Advisory) : IForgeEvent;
