using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions.Views;

namespace MintPlayer.Spark.Messaging.Models;

/// <summary>A message's delivery state: what the feeder's page and the retry sweeper filter and sort on.</summary>
public sealed class SparkMessageState
{
    public string? Id { get; set; }
    public string QueueName { get; set; } = string.Empty;
    public EMessageStatus Status { get; set; }
    public int Priority { get; set; }
    public bool Expedite { get; set; }
    public DateTime? QueuedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public bool WakeUp { get; set; }
    public DateTime? ClaimExpiresAtUtc { get; set; }
}

/// <summary>
/// The view the feeder's page and the retry sweeper query (#536 D42): a RavenDB static index, an SQL view, LINQ in
/// memory. A plain collection query would do on every store but RavenDB, where it makes the server build an auto index
/// on first use, which an application cannot deploy ahead of time and which CodeCoverage forbids (D-O22).
/// </summary>
public sealed class SparkMessages_ByState : SparkView<SparkMessageState>
{
    public override Expression<Func<ISparkViewSource, IQueryable<SparkMessageState>>> Map => s =>
        s.Set<SparkMessage>().Select(m => new SparkMessageState
        {
            Id = s.IdOf(m),
            QueueName = m.QueueName,
            Status = m.Status,
            Priority = m.Priority,
            Expedite = m.Expedite,
            QueuedAtUtc = m.QueuedAtUtc,
            CreatedAtUtc = m.CreatedAtUtc,
            NextAttemptAtUtc = m.NextAttemptAtUtc,
            WakeUp = m.WakeUp,
            ClaimExpiresAtUtc = m.ClaimExpiresAtUtc,
        });
}
