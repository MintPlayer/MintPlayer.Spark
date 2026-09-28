using MintPlayer.Spark.Moderation.Documents;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Moderation.Indexes;

// Hand-written map-reduce indexes (#460 §3.12): [GenerateIndex] cannot reduce. They derive from
// RavenDB's AbstractIndexCreationTask<T, R> directly — SparkIndexCreationTask<T> only applies
// generated [Search]/DateTimeOffset field options, which none of these have (spike S-MOD-A: no
// SPARK018 diagnostic, deployed by core's index creation from this add-on assembly).
//
// ⚠️ Absent-field rule: "Credited != true", never "!Credited" / "== false" (an entry written before a
// field existed has no value, and RavenDB does not match an absent field against false). No
// ".HasValue" anywhere: on a static index it is a 500.

/// <summary>
/// Credited ledger entries summed per (recipient, voter, day). The reputation total is the sum of a
/// user's rows; the diversity rule counts the distinct voters and days among rows with votes.
/// </summary>
public sealed class Moderation_ReputationCells : AbstractIndexCreationTask<ReputationEvent, Moderation_ReputationCells.Result>
{
    public sealed class Result
    {
        public string UserId { get; set; } = string.Empty;
        /// <summary>Empty for entries not derived from a vote (flag outcomes).</summary>
        public string VoterId { get; set; } = string.Empty;
        public string Day { get; set; } = string.Empty;
        public int Points { get; set; }
        public int Votes { get; set; }
    }

    public Moderation_ReputationCells()
    {
        Map = events => from e in events
                        where e.Credited == true
                        select new Result
                        {
                            UserId = e.UserId,
                            VoterId = e.VoterId ?? string.Empty,
                            Day = e.Day,
                            Points = e.Points,
                            Votes = e.VoteWeight,
                        };

        Reduce = results => from r in results
                            group r by new { r.UserId, r.VoterId, r.Day } into g
                            select new Result
                            {
                                UserId = g.Key.UserId,
                                VoterId = g.Key.VoterId,
                                Day = g.Key.Day,
                                Points = g.Sum(x => x.Points),
                                Votes = g.Sum(x => x.Votes),
                            };
    }
}

/// <summary>Not-yet-credited points per user (shown as "pending" beside the badge).</summary>
public sealed class Moderation_PendingReputation : AbstractIndexCreationTask<ReputationEvent, Moderation_PendingReputation.Result>
{
    public sealed class Result
    {
        public string UserId { get; set; } = string.Empty;
        public int Points { get; set; }
    }

    public Moderation_PendingReputation()
    {
        Map = events => from e in events
                        where e.Credited != true
                        select new Result { UserId = e.UserId, Points = e.Points };

        Reduce = results => from r in results
                            group r by r.UserId into g
                            select new Result { UserId = g.Key, Points = g.Sum(x => x.Points) };
    }
}

/// <summary>Ledger entries the crediting job has not credited yet, by when they become creditable.</summary>
public sealed class Moderation_EntriesToCredit : AbstractIndexCreationTask<ReputationEvent>
{
    public sealed class Result
    {
        public DateTime CreditableAfterUtc { get; set; }
    }

    public Moderation_EntriesToCredit()
    {
        Map = events => from e in events
                        where e.Credited != true
                        select new Result { CreditableAfterUtc = e.CreditableAfterUtc };
    }
}

/// <summary>
/// Active votes per (voter, recipient, day): the voter→target matrix the nightly detector reads
/// (serial voting, concentration, reciprocal pairs) and the review surface shows.
/// </summary>
public sealed class Moderation_VotePairs : AbstractIndexCreationTask<ModerationVote, Moderation_VotePairs.Result>
{
    public sealed class Result
    {
        public string VoterId { get; set; } = string.Empty;
        public string AuthorId { get; set; } = string.Empty;
        public string Day { get; set; } = string.Empty;
        public int DayNumber { get; set; }
        public int Count { get; set; }
        public int Up { get; set; }
        public int Down { get; set; }
    }

    public Moderation_VotePairs()
    {
        Map = votes => from v in votes
                       where v.Direction != 0 && v.AuthorId != null
                       select new Result
                       {
                           VoterId = v.VoterId,
                           AuthorId = v.AuthorId!,
                           Day = v.Day,
                           DayNumber = v.DayNumber,
                           Count = 1,
                           Up = v.Direction > 0 ? 1 : 0,
                           Down = v.Direction < 0 ? 1 : 0,
                       };

        Reduce = results => from r in results
                            group r by new { r.VoterId, r.AuthorId, r.Day, r.DayNumber } into g
                            select new Result
                            {
                                VoterId = g.Key.VoterId,
                                AuthorId = g.Key.AuthorId,
                                Day = g.Key.Day,
                                DayNumber = g.Key.DayNumber,
                                Count = g.Sum(x => x.Count),
                                Up = g.Sum(x => x.Up),
                                Down = g.Sum(x => x.Down),
                            };
    }
}

/// <summary>The index names Moderation deploys — the deployment assertion checks every one.</summary>
public static class ModerationIndexNames
{
    public static readonly IReadOnlyList<string> All =
    [
        new Moderation_ReputationCells().IndexName,
        new Moderation_PendingReputation().IndexName,
        new Moderation_EntriesToCredit().IndexName,
        new Moderation_VotePairs().IndexName,
    ];
}
