using System.Globalization;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Moderation.Documents;

/// <summary>Deterministic document ids — the "at most one" rules live here.</summary>
/// <remarks>
/// Lower case (#536 D7): a document written before is still found, because RavenDB loads an id ignoring case, and none
/// is re-keyed. The ids stored inside documents (<see cref="ModerationVote.EventIds"/>, <c>CaseId</c>, <c>VoteId</c>,
/// <c>CompensatesId</c>) keep the casing they were written with, so code comparing one of them in memory with an id
/// from here compares ignoring case.
/// </remarks>
public static class ModerationIds
{
    /// <summary>What a deleted account's ids are replaced with in the ledger.</summary>
    public const string DeletedUser = "deleted-user";

    public static Id<ModerationVote> Vote(string voterId, string targetId) => Id<ModerationVote>.FromTrusted($"moderationvotes/{voterId}/{targetId}");
    public static Id<ModerationTally> Tally(string targetId) => Id<ModerationTally>.FromTrusted($"moderationtallies/{targetId}");
    public static Id<ModerationFlag> Flag(string flaggerId, string targetId) => Id<ModerationFlag>.FromTrusted($"moderationflags/{flaggerId}/{targetId}");
    public static Id<ReviewCase> FlagCase(string targetId) => Id<ReviewCase>.FromTrusted($"moderationcases/flag/{targetId}");
    public static Id<ReviewCase> FraudCase(string ruleId, string a, string b) => Id<ReviewCase>.FromTrusted($"moderationcases/{ruleId}/{a}/{b}");
    public static Id<ModerationLock> Lock(string targetId) => Id<ModerationLock>.FromTrusted($"moderationlocks/{targetId}");
    public static Id<ModerationSuspension> Suspension(string userId) => Id<ModerationSuspension>.FromTrusted($"moderationsuspensions/{userId}");
    public static Id<ModerationProfile> Profile(string userId) => Id<ModerationProfile>.FromTrusted($"moderationprofiles/{userId}");
    public static Id<ReputationSummary> Summary(string userId) => Id<ReputationSummary>.FromTrusted($"moderationreputation/{userId}");

    /// <summary>A vote's ledger entry: <c>{voteId}/{revision}/{role}</c> (role <c>recipient</c> or <c>voter</c>).</summary>
    public static Id<ReputationEvent> VoteEvent(string voteId, int revision, string role)
        => Id<ReputationEvent>.FromTrusted($"reputationevents/{voteId}/{revision.ToString(CultureInfo.InvariantCulture)}/{role}");

    /// <summary>The one compensation an entry can have — the id is the idempotency (fraud measure 7).</summary>
    public static Id<ReputationEvent> Compensation(string originalEventId) => Id<ReputationEvent>.FromTrusted($"{originalEventId}/compensation");

    public static Id<ReputationEvent> FlagEvent(string flagId, string outcome) => Id<ReputationEvent>.FromTrusted($"reputationevents/{flagId}/{outcome}");

    public static Id<ModerationCounter> CastCounter(string voterId, string day) => Id<ModerationCounter>.FromTrusted($"moderationcounters/cast/{voterId}/{day}");
    public static Id<ModerationCounter> RecipientCounter(string userId, string day) => Id<ModerationCounter>.FromTrusted($"moderationcounters/received/{userId}/{day}");
    public static Id<ModerationCounter> PairCounter(string voterId, string authorId, string day) => Id<ModerationCounter>.FromTrusted($"moderationcounters/pair/{voterId}/{authorId}/{day}");
    public static Id<ModerationCounter> PostCounter(string userId, string day) => Id<ModerationCounter>.FromTrusted($"moderationcounters/posts/{userId}/{day}");

    public static Id<ModerationIpObservation> IpObservation(string userId, string day, string hash) => Id<ModerationIpObservation>.FromTrusted($"moderationipobservations/{userId}/{day}/{hash[..Math.Min(16, hash.Length)]}");
    public static Id<ModerationIpKey> IpKey(long period) => Id<ModerationIpKey>.FromTrusted($"moderationipkeys/{period.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The UTC day of <paramref name="utc"/> as <c>yyyy-MM-dd</c>.</summary>
    public static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Whole UTC days since 1970-01-01 of <paramref name="utc"/>.</summary>
    public static int DayNumber(DateTime utc) => (int)(utc.Date - DateTime.UnixEpoch).TotalDays;
}
