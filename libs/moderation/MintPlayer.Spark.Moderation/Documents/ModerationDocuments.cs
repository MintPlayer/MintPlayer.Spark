namespace MintPlayer.Spark.Moderation.Documents;

// Moderation's data are plain RavenDB documents behind /spark/moderation/* (#460, T6), not persistent
// objects: an add-on assembly cannot own model JSON or security.json entries. Ids are deterministic
// wherever "at most one" is a rule (one vote per voter and target, one flag per flagger and target,
// one reversal per original entry), so the rule is enforced by the id, not by a query.

/// <summary>A voter's vote on one target. Id: <see cref="ModerationIds.Vote"/>.</summary>
public sealed class ModerationVote
{
    public string? Id { get; set; }
    public string VoterId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    /// <summary>The target's author at the time of the vote — the recipient of its reputation.</summary>
    public string? AuthorId { get; set; }
    /// <summary>+1, −1, or 0 once withdrawn (the document stays, so its id keeps the "one vote" rule).</summary>
    public int Direction { get; set; }
    public DateTime CastAtUtc { get; set; }
    /// <summary>The UTC day of <see cref="CastAtUtc"/> (yyyy-MM-dd), the index's time bucket.</summary>
    public string Day { get; set; } = string.Empty;
    /// <summary>Days since 1970-01-01 of <see cref="CastAtUtc"/> — the index filters on it (RavenDB LINQ cannot translate string.Compare, measured).</summary>
    public int DayNumber { get; set; }
    public DateTime? TargetPostedAtUtc { get; set; }
    /// <summary>Bumped on every change; part of the ledger entries' ids.</summary>
    public int Revision { get; set; }
    /// <summary>The ledger entries of the current direction (the recipient's and, for a down-vote, the voter's).</summary>
    public List<string> EventIds { get; set; } = [];
}

/// <summary>
/// One entry of the reputation ledger. Never deleted and never edited except for the
/// <see cref="Credited"/> and <see cref="CompensatedById"/> flags: a correction is a compensating
/// entry (<see cref="ReputationEventKinds.Reversal"/> / <see cref="ReputationEventKinds.Retraction"/>).
/// </summary>
public sealed class ReputationEvent
{
    public string? Id { get; set; }
    /// <summary>Whose reputation this changes. <see cref="ModerationIds.DeletedUser"/> once that account is deleted.</summary>
    public string UserId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Points { get; set; }
    /// <summary>
    /// For vote-derived entries: 1 when this entry counts as one credited vote for the diversity rule
    /// (an eligible, uncapped vote received), −1 on its compensation, else 0.
    /// </summary>
    public int VoteWeight { get; set; }
    /// <summary>The voter, for vote-derived entries. Never shown to the recipient.</summary>
    public string? VoterId { get; set; }
    public string? VoteId { get; set; }
    public string? TargetId { get; set; }
    public string? TargetType { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>The UTC day (yyyy-MM-dd) the original vote was cast; a compensation keeps the original's day.</summary>
    public string Day { get; set; } = string.Empty;
    /// <summary>Delayed crediting (fraud measure 5): not counted anywhere until the crediting job has passed this instant.</summary>
    public DateTime CreditableAfterUtc { get; set; }
    public bool Credited { get; set; }
    /// <summary>Why a vote-derived entry carries no points: <c>ineligible-voter</c>, <c>pair-cap</c>, <c>daily-cap</c>.</summary>
    public string? ZeroedBy { get; set; }
    /// <summary>On a compensation: the entry it compensates.</summary>
    public string? CompensatesId { get; set; }
    /// <summary>On an original: its compensation, once written (so an entry is compensated at most once).</summary>
    public string? CompensatedById { get; set; }
    /// <summary>On a reversal: the rule that fired (<c>serial</c>, <c>concentration</c>, <c>moderator</c>, <c>merge</c>, <c>account-deleted</c>, <c>content-deleted</c>).</summary>
    public string? RuleId { get; set; }
    public string? CaseId { get; set; }
}

/// <summary>The score of one target, kept beside it (never on it: a vote must not move the target's etag). Id: <see cref="ModerationIds.Tally"/>.</summary>
public sealed class ModerationTally
{
    public string? Id { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public int Up { get; set; }
    public int Down { get; set; }
    public int Score => Up - Down;
}

/// <summary>An exact per-day counter for the write-path caps. Id: <see cref="ModerationIds.Counter"/>; expires after 31 days.</summary>
public sealed class ModerationCounter
{
    public string? Id { get; set; }
    public int Count { get; set; }
}

/// <summary>Per-user activity (the minActiveDays gate). Id: <see cref="ModerationIds.Profile"/>.</summary>
public sealed class ModerationProfile
{
    public string? Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int ActiveDays { get; set; }
    public string? LastActiveDay { get; set; }
}

/// <summary>A user's reputation as the privilege provider reads it, recomputed from the ledger index. Id: <see cref="ModerationIds.Summary"/>.</summary>
public sealed class ReputationSummary
{
    public string? Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    /// <summary>Sum of every credited entry — the number shown on the badge.</summary>
    public int Total { get; set; }
    /// <summary>
    /// Sum of entries not yet credited (delayed crediting), as of the last recompute. A vote does not
    /// recompute, so this lags; the badge (<c>ISparkModeration.GetReputationAsync</c>) reads pending
    /// live from the index instead.
    /// </summary>
    public int Pending { get; set; }
    /// <summary>What counts toward privileges: <see cref="Total"/>, minus the vote-derived part when the diversity rule fails.</summary>
    public int PrivilegeReputation { get; set; }
    public int CreditedVotes { get; set; }
    public int DistinctVoters { get; set; }
    public int DistinctDays { get; set; }
    public bool DiversityMet { get; set; }
    public DateTime ComputedAtUtc { get; set; }
}

/// <summary>A flag raised on a target. Id: <see cref="ModerationIds.Flag"/> (one per flagger and target).</summary>
public sealed class ModerationFlag
{
    public string? Id { get; set; }
    public string FlaggerId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime RaisedAtUtc { get; set; }
    public string CaseId { get; set; } = string.Empty;
    /// <summary><c>open</c>, <c>upheld</c>, <c>declined</c>.</summary>
    public string Status { get; set; } = ModerationCaseStatus.Open;
}

public static class ModerationCaseStatus
{
    public const string Open = "open";
    public const string Upheld = "upheld";
    public const string Declined = "declined";
    public const string Dismissed = "dismissed";
    public const string Reversed = "reversed";
    public const string Merged = "merged";
    public const string Suspended = "suspended";
}

/// <summary>A case in the review queue: a flagged target, or accounts a fraud rule wants a human to look at.</summary>
public sealed class ReviewCase
{
    public string? Id { get; set; }
    /// <summary><c>flag</c>, or the fraud rule id (<c>reciprocal</c>, <c>fast-voting</c>, <c>registration-cluster</c>).</summary>
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = ModerationCaseStatus.Open;
    public string? TargetId { get; set; }
    public string? TargetType { get; set; }
    /// <summary>The accounts involved (fraud cases): typically a voter and a recipient.</summary>
    public List<string> AccountIds { get; set; } = [];
    public List<string> VoteIds { get; set; } = [];
    public int FlagCount { get; set; }
    public string? Summary { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public string? Decision { get; set; }
}

/// <summary>A locked target. Id: <see cref="ModerationIds.Lock"/>.</summary>
public sealed class ModerationLock
{
    public string? Id { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? LockedBy { get; set; }
    public DateTime LockedAtUtc { get; set; }
}

/// <summary>A suspended account. Id: <see cref="ModerationIds.Suspension"/>; read by id, so it is effective instantly.</summary>
public sealed class ModerationSuspension
{
    public string? Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    /// <summary>Null: until lifted.</summary>
    public DateTime? UntilUtc { get; set; }
    public string? Reason { get; set; }
    public string? SuspendedBy { get; set; }
    public DateTime SuspendedAtUtc { get; set; }
    public string? CaseId { get; set; }
}

/// <summary>One moderation decision, for the audit log.</summary>
public sealed class ModerationAuditEntry
{
    public string? Id { get; set; }
    public string? ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public string? TargetType { get; set; }
    public string? SubjectUserId { get; set; }
    public string? CaseId { get; set; }
    public string? Reason { get; set; }
    public string? Details { get; set; }
    public DateTime AtUtc { get; set; }
}

/// <summary>
/// "This account was seen from this network" (fraud measure 9): an HMAC of the truncated address
/// (/24 IPv4, /48 IPv6) under a rotated server-side key — never the address. Expires after 90 days.
/// </summary>
public sealed class ModerationIpObservation
{
    public string? Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Day { get; set; } = string.Empty;
}

/// <summary>One period's HMAC key, stored Data-Protection-protected (the key ring lives outside the database).</summary>
public sealed class ModerationIpKey
{
    public string? Id { get; set; }
    public string ProtectedKey { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
