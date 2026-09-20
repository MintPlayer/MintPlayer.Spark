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
    DateTimeOffset? AuthoredAt);

/// <summary>
/// A pull request was opened, reopened, or had new commits pushed to it.
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
    bool AuthorIsBot);

/// <summary>A pull request was merged.</summary>
/// <param name="HeadRef">The source branch, for the optional delete-after-merge courtesy.</param>
/// <remarks>
/// ⚠️ <b>Merged, not closed.</b> The two are not interchangeable: retention surrenders a merged pull
/// request's build data, while a closed-unmerged one keeps it, because it may reopen and nothing
/// about it is final. A forge that reports only "closed" must determine mergedness before raising
/// this, and raise nothing if it cannot — deleting builds on a guess is unrecoverable.
/// </remarks>
public sealed record PullRequestMerged(
    string RepositoryId,
    int Number,
    string? HeadRef);

/// <summary>A repository changed its name or moved to a different owner.</summary>
/// <remarks>
/// The stored document keeps its id — the forge's numeric id is stable across a rename, which is
/// exactly why ids are keyed on it rather than on the name. ⚠️ Bitbucket slugs are renameable
/// <em>and reusable</em>, so a Bitbucket implementation must not treat a name as an identity.
/// </remarks>
public sealed record RepositoryRenamed(
    string RepositoryId,
    string NewName,
    string NewFullName,
    string NewOwnerLogin);

/// <summary>An account (user or organisation) changed its login.</summary>
public sealed record OwnerRenamed(
    string AccountId,
    string NewLogin);

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
public sealed record RepositoryConnectionChanged(
    string RepositoryId,
    bool Connected,
    string? Reason);
