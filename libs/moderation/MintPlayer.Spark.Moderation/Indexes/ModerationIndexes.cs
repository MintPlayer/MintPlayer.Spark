using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions.Views;
using MintPlayer.Spark.Moderation.Documents;

namespace MintPlayer.Spark.Moderation.Indexes;

// Moderation's views (#536 S16, D43): declared once, a RavenDB static (map-reduce) index, an SQL view (GROUP BY for a
// reduce) and LINQ in memory. Every query moderation runs reads one of them, never a collection (D42: a collection
// query makes RavenDB build an auto index). A row-per-entity view carries the document's id; the caller loads the
// documents by id in one batch.
//
// ⚠️ Absent-field rule: "Credited != true", never "!Credited" / "== false" (an entry written before a field existed
// has no value, and RavenDB does not match an absent field against false). No ".HasValue" anywhere: on a RavenDB
// static index it is a 500.

/// <summary>
/// Credited ledger entries summed per (recipient, voter, day). The reputation total is the sum of a user's rows; the
/// diversity rule counts the distinct voters and days among rows with votes.
/// </summary>
public sealed class Moderation_ReputationCells : SparkView<Moderation_ReputationCells.Result>
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

    public override Expression<Func<ISparkViewSource, IQueryable<Result>>> Map => s =>
        from e in s.Set<ReputationEvent>()
        where e.Credited == true
        select new Result { UserId = e.UserId, VoterId = e.VoterId ?? string.Empty, Day = e.Day, Points = e.Points, Votes = e.VoteWeight };

    public override Expression<Func<IEnumerable<Result>, IEnumerable<Result>>> Reduce => results =>
        from r in results
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

/// <summary>Not-yet-credited points per user (shown as "pending" beside the badge).</summary>
public sealed class Moderation_PendingReputation : SparkView<Moderation_PendingReputation.Result>
{
    public sealed class Result
    {
        public string UserId { get; set; } = string.Empty;
        public int Points { get; set; }
    }

    public override Expression<Func<ISparkViewSource, IQueryable<Result>>> Map => s =>
        from e in s.Set<ReputationEvent>()
        where e.Credited != true
        select new Result { UserId = e.UserId, Points = e.Points };

    public override Expression<Func<IEnumerable<Result>, IEnumerable<Result>>> Reduce => results =>
        from r in results
        group r by r.UserId into g
        select new Result { UserId = g.Key, Points = g.Sum(x => x.Points) };
}

/// <summary>
/// Active votes per (voter, recipient, day): the voter→target matrix the nightly detector reads (serial voting,
/// concentration, reciprocal pairs) and the review surface shows.
/// </summary>
public sealed class Moderation_VotePairs : SparkView<Moderation_VotePairs.Result>
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

    public override Expression<Func<ISparkViewSource, IQueryable<Result>>> Map => s =>
        from v in s.Set<ModerationVote>()
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

    public override Expression<Func<IEnumerable<Result>, IEnumerable<Result>>> Reduce => results =>
        from r in results
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

/// <summary>Every vote by voter, target and time: account deletion, reversals and the detector's window.</summary>
public sealed class Moderation_Votes : SparkView<Moderation_Votes.Result>
{
    public sealed class Result
    {
        public string? Id { get; set; }
        public string VoterId { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public string? AuthorId { get; set; }
        public int Direction { get; set; }
        public DateTime CastAtUtc { get; set; }
        public DateTime? TargetPostedAtUtc { get; set; }
    }

    public override Expression<Func<ISparkViewSource, IQueryable<Result>>> Map => s =>
        s.Set<ModerationVote>().Select(v => new Result
        {
            Id = s.IdOf(v),
            VoterId = v.VoterId,
            TargetId = v.TargetId,
            AuthorId = v.AuthorId,
            Direction = v.Direction,
            CastAtUtc = v.CastAtUtc,
            TargetPostedAtUtc = v.TargetPostedAtUtc,
        });
}

/// <summary>Every flag by flagger, case and status.</summary>
public sealed class Moderation_Flags : SparkView<Moderation_Flags.Result>
{
    public sealed class Result
    {
        public string? Id { get; set; }
        public string FlaggerId { get; set; } = string.Empty;
        public string CaseId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public override Expression<Func<ISparkViewSource, IQueryable<Result>>> Map => s =>
        s.Set<ModerationFlag>().Select(f => new Result { Id = s.IdOf(f), FlaggerId = f.FlaggerId, CaseId = f.CaseId, Status = f.Status });
}

/// <summary>
/// Every ledger entry by recipient, voter, time and crediting state: a user's history, account deletion, and the
/// entries the crediting job credits (<c>Credited != true &amp;&amp; CreditableAfterUtc &lt;= now</c>; it replaced the
/// <c>Moderation_EntriesToCredit</c> index, as one entity type has one row-per-entity view).
/// </summary>
public sealed class Moderation_Ledger : SparkView<Moderation_Ledger.Result>
{
    public sealed class Result
    {
        public string? Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string? VoterId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public bool Credited { get; set; }
        public DateTime CreditableAfterUtc { get; set; }
    }

    public override Expression<Func<ISparkViewSource, IQueryable<Result>>> Map => s =>
        s.Set<ReputationEvent>().Select(e => new Result
        {
            Id = s.IdOf(e),
            UserId = e.UserId,
            VoterId = e.VoterId,
            CreatedAtUtc = e.CreatedAtUtc,
            Credited = e.Credited,
            CreditableAfterUtc = e.CreditableAfterUtc,
        });
}

/// <summary>Every IP observation by user and hash: shared networks (fraud measure 9).</summary>
public sealed class Moderation_IpObservations : SparkView<Moderation_IpObservations.Result>
{
    public sealed class Result
    {
        public string? Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }

    public override Expression<Func<ISparkViewSource, IQueryable<Result>>> Map => s =>
        s.Set<ModerationIpObservation>().Select(o => new Result { Id = s.IdOf(o), UserId = o.UserId, KeyId = o.KeyId, Hash = o.Hash });
}

/// <summary>Every review case by status and opening time: the review queue.</summary>
public sealed class Moderation_Cases : SparkView<Moderation_Cases.Result>
{
    public sealed class Result
    {
        public string? Id { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime OpenedAtUtc { get; set; }
    }

    public override Expression<Func<ISparkViewSource, IQueryable<Result>>> Map => s =>
        s.Set<ReviewCase>().Select(c => new Result { Id = s.IdOf(c), Status = c.Status, OpenedAtUtc = c.OpenedAtUtc });
}

/// <summary>Every audit entry by time: the audit log.</summary>
public sealed class Moderation_Audit : SparkView<Moderation_Audit.Result>
{
    public sealed class Result
    {
        public string? Id { get; set; }
        public DateTime AtUtc { get; set; }
    }

    public override Expression<Func<ISparkViewSource, IQueryable<Result>>> Map => s =>
        s.Set<ModerationAuditEntry>().Select(e => new Result { Id = s.IdOf(e), AtUtc = e.AtUtc });
}

/// <summary>The view (index) names Moderation deploys — the deployment assertion checks every one.</summary>
public static class ModerationIndexNames
{
    public static readonly IReadOnlyList<string> All =
    [
        nameof(Moderation_ReputationCells),
        nameof(Moderation_PendingReputation),
        nameof(Moderation_VotePairs),
        nameof(Moderation_Votes),
        nameof(Moderation_Flags),
        nameof(Moderation_Ledger),
        nameof(Moderation_IpObservations),
        nameof(Moderation_Cases),
        nameof(Moderation_Audit),
    ];
}
