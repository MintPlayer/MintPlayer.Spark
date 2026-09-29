using System.Globalization;

namespace MintPlayer.Spark.Moderation.Documents;

/// <summary>Deterministic document ids — the "at most one" rules live here.</summary>
public static class ModerationIds
{
    /// <summary>What a deleted account's ids are replaced with in the ledger.</summary>
    public const string DeletedUser = "deleted-user";

    public static string Vote(string voterId, string targetId) => $"ModerationVotes/{voterId}/{targetId}";
    public static string Tally(string targetId) => $"ModerationTallies/{targetId}";
    public static string Flag(string flaggerId, string targetId) => $"ModerationFlags/{flaggerId}/{targetId}";
    public static string FlagCase(string targetId) => $"ModerationCases/flag/{targetId}";
    public static string FraudCase(string ruleId, string a, string b) => $"ModerationCases/{ruleId}/{a}/{b}";
    public static string Lock(string targetId) => $"ModerationLocks/{targetId}";
    public static string Suspension(string userId) => $"ModerationSuspensions/{userId}";
    public static string Profile(string userId) => $"ModerationProfiles/{userId}";
    public static string Summary(string userId) => $"ModerationReputation/{userId}";

    /// <summary>A vote's ledger entry: <c>{voteId}/{revision}/{role}</c> (role <c>recipient</c> or <c>voter</c>).</summary>
    public static string VoteEvent(string voteId, int revision, string role)
        => $"ReputationEvents/{voteId}/{revision.ToString(CultureInfo.InvariantCulture)}/{role}";

    /// <summary>The one compensation an entry can have — the id is the idempotency (fraud measure 7).</summary>
    public static string Compensation(string originalEventId) => $"{originalEventId}/compensation";

    public static string FlagEvent(string flagId, string outcome) => $"ReputationEvents/{flagId}/{outcome}";

    public static string CastCounter(string voterId, string day) => $"ModerationCounters/cast/{voterId}/{day}";
    public static string RecipientCounter(string userId, string day) => $"ModerationCounters/received/{userId}/{day}";
    public static string PairCounter(string voterId, string authorId, string day) => $"ModerationCounters/pair/{voterId}/{authorId}/{day}";
    public static string PostCounter(string userId, string day) => $"ModerationCounters/posts/{userId}/{day}";

    public static string IpObservation(string userId, string day, string hash) => $"ModerationIpObservations/{userId}/{day}/{hash[..Math.Min(16, hash.Length)]}";
    public static string IpKey(long period) => $"ModerationIpKeys/{period.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The UTC day of <paramref name="utc"/> as <c>yyyy-MM-dd</c>.</summary>
    public static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Whole UTC days since 1970-01-01 of <paramref name="utc"/>.</summary>
    public static int DayNumber(DateTime utc) => (int)(utc.Date - DateTime.UnixEpoch).TotalDays;
}
