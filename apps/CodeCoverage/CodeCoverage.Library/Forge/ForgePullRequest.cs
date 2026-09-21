namespace CodeCoverage.Forge;

/// <summary>
/// A pull request as the forge reports it, read at the moment we need to trust it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists to replace uploader-supplied values with forge-supplied ones.</b> An upload
/// arrives carrying a repository name, a sha, a branch and a pull-request number, and every one of
/// those is a string the caller chose. For an authenticated upload that is acceptable — the
/// credential already binds the caller to the repository. For a fork upload, which by construction
/// carries no credential at all, it is not: the branch decides which badge is served and the sha
/// decides what a later build compares against, so a caller who picks both picks the answer.
/// </para>
/// <para>
/// Reading the pull request turns all four into derived values. The sha must equal
/// <see cref="HeadSha"/>, the branch is <see cref="HeadRef"/> rather than whatever was posted, and
/// <see cref="IsFromFork"/> is a fact rather than a claim. That is the whole point of the round
/// trip, and it is why the result must never be cached across a head change.
/// </para>
/// <para>
/// ⚠️ <see cref="Number"/> is the number a human sees and types into a URL. On GitHub that is the
/// pull request's <c>number</c>; on GitLab it is a merge request's <c>iid</c>, <b>not</b> its
/// <c>id</c>, which is globally unique and meaningless to the user. A GitLab implementation that
/// fills this from <c>id</c> produces feedback posted against the wrong merge request.
/// </para>
/// </remarks>
/// <param name="Number">The pull request's user-facing number within its target repository.</param>
/// <param name="HeadSha">The current head commit. An upload naming any other sha is not this pull request's.</param>
/// <param name="HeadRef">The source branch name, with no <c>refs/heads/</c> prefix.</param>
/// <param name="HeadRepositoryId">
/// The forge's numeric id for the repository the branch lives in, or null when the forge no longer
/// reports one — a deleted fork is the ordinary cause.
/// <para>
/// ⚠️ Null is <b>not</b> "same repository". Treating it as such is how a fork upload would be
/// accepted as a first-party one, so <see cref="IsFromFork"/> deliberately reports true for it:
/// the safe reading of "we cannot tell" is the more restricted one.
/// </para>
/// </param>
/// <param name="BaseRef">The branch the pull request targets.</param>
/// <param name="BaseRepositoryId">The forge's numeric id for the target repository.</param>
/// <param name="BaseRepositoryDefaultBranch">
/// The target repository's default branch, as the forge reports it right now. Carried here because
/// the caller that needs it most — an upload against a repository the app only just learned about —
/// is exactly the one whose stored copy is still null.
/// </param>
/// <param name="IsOpen">
/// Whether the pull request is still open. Coverage for a closed one is not refused on this alone:
/// a run can finish after a merge, and discarding its report would lose the number for the commit
/// that was actually merged.
/// </param>
public sealed record ForgePullRequest(
    int Number,
    string HeadSha,
    string HeadRef,
    long? HeadRepositoryId,
    string BaseRef,
    long BaseRepositoryId,
    string? BaseRepositoryDefaultBranch,
    bool IsOpen)
{
    /// <summary>
    /// Whether the head branch lives in a different repository than the target.
    /// </summary>
    /// <remarks>
    /// ⚠️ Reports true when <see cref="HeadRepositoryId"/> is null, for the reason given there.
    /// This is the only fork signal the upload path has — the action cannot supply one, because a
    /// fork runner is not told it is one in any form the server could verify.
    /// </remarks>
    public bool IsFromFork => HeadRepositoryId is not { } head || head != BaseRepositoryId;
}
