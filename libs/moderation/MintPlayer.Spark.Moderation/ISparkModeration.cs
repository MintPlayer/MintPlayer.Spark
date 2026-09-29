namespace MintPlayer.Spark.Moderation;

/// <summary>
/// Moderation as a service — what the <c>/spark/moderation/*</c> endpoints call, for application
/// code that needs the same operations. Every member authorizes the <b>current</b> user exactly as
/// the endpoint does: a target the caller cannot see is a <c>SparkRowLevelAccessDeniedException</c>
/// (answered 404), a missing right a <c>SparkAccessDeniedException</c>, a rule a
/// <c>SparkValidationException</c> (400) and a quota a <c>SparkThrottledException</c> (429).
/// </summary>
public interface ISparkModeration
{
    /// <summary>Casts (+1 / −1) or withdraws (0) the current user's vote. Requires <c>Vote/T</c> or <c>Downvote/T</c>.</summary>
    Task<ModerationVoteState> VoteAsync(Guid objectTypeId, string id, int direction, CancellationToken cancellationToken = default);

    /// <summary>The score and the current user's vote for each visible id (invisible ids are left out).</summary>
    Task<IReadOnlyList<ModerationVoteState>> GetVotesAsync(Guid objectTypeId, IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);

    /// <summary>Flags a target for review. Requires <c>Flag/T</c>. Idempotent per user and target.</summary>
    Task FlagAsync(Guid objectTypeId, string id, string reason, CancellationToken cancellationToken = default);

    /// <summary>Locks a target: saves and deletes are refused (400) for everyone without <c>Lock/T</c>.</summary>
    Task LockAsync(Guid objectTypeId, string id, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Lifts a lock. Requires <c>Lock/T</c>.</summary>
    Task UnlockAsync(Guid objectTypeId, string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suspends an account: Identity lockout + security-stamp refresh, an immediate server-side write
    /// block, and no reputation groups. Requires <c>Suspend/Moderation</c>.
    /// </summary>
    Task SuspendAsync(string userId, int? days, string? reason, string? caseId = null, CancellationToken cancellationToken = default);

    /// <summary>Lifts a suspension. Requires <c>Suspend/Moderation</c>.</summary>
    Task UnsuspendAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Treats <paramref name="duplicateId"/> as a sock puppet of <paramref name="intoId"/>: every vote
    /// the duplicate cast is reversed (rule <c>merge</c>). Requires <c>Suspend/Moderation</c>.
    /// </summary>
    Task MergeAccountsAsync(string duplicateId, string intoId, string? caseId = null, CancellationToken cancellationToken = default);

    /// <summary>A user's reputation (total, pending, earned privileges).</summary>
    Task<ModerationReputation> GetReputationAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides a review case. Flag cases: <c>uphold</c> / <c>decline</c>. Fraud cases: <c>dismiss</c>,
    /// <c>reverse</c> (the case's votes), <c>merge</c> (needs <paramref name="accountId"/>, the duplicate)
    /// and <c>suspend</c> (escalate; needs <paramref name="accountId"/>). Requires <c>Review/Moderation</c>
    /// (and <c>Suspend/Moderation</c> for merge and suspend). Audited.
    /// </summary>
    Task DecideAsync(string caseId, string decision, string? accountId = null, string? reason = null, CancellationToken cancellationToken = default);
}

/// <summary>A target's score and the caller's vote.</summary>
public sealed class ModerationVoteState
{
    public required string Id { get; init; }
    public int Score { get; init; }
    public int Up { get; init; }
    public int Down { get; init; }
    /// <summary>The caller's vote: +1, −1 or 0.</summary>
    public int MyVote { get; init; }
    public bool Locked { get; init; }

    /// <summary>
    /// Whether the caller holds <c>Vote</c> on the target's type (an earned privilege or a group
    /// grant), so a widget can disable the arrow instead of offering a click the server refuses. Not
    /// a promise the vote is accepted: an own post, a lock or a suspension still refuse it.
    /// </summary>
    public bool CanUpvote { get; init; }

    /// <summary>As <see cref="CanUpvote"/>, for <c>Downvote</c>.</summary>
    public bool CanDownvote { get; init; }
}

/// <summary>A user's reputation as shown on a badge.</summary>
public sealed class ModerationReputation
{
    public required string UserId { get; init; }
    public int Total { get; init; }
    public int Pending { get; init; }
    public IReadOnlyList<string> Privileges { get; init; } = [];
    public bool Suspended { get; init; }
}
