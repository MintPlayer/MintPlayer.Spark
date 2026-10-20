using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Printing.Models;

namespace MintPlayer.Spark.Printing.Indexes;

/// <summary>
/// The printing package's one static index (PRD §4.5, index budget: <c>Printing_Jobs</c>), declared as data (commerce X19):
/// an agent's queue, a station's queue and fallbacks, the jobs grid. Point loads need none.
/// </summary>
public static class PrintingIndexes
{
    /// <summary>The deployed name (the generator turns <c>_</c> into <c>/</c>).</summary>
    public const string Jobs = "Printing/Jobs";

    [SparkIndexMap("Printing_Jobs")]
    public static Expression<Func<IEnumerable<PrintJob>, IEnumerable<PrintJobEntry>>> JobsMap { get; } =
        jobs => from j in jobs
                select new PrintJobEntry
                {
                    Printer = j.Printer,
                    Adapter = j.Adapter,
                    Agent = j.Agent,
                    Site = j.Site,
                    Station = j.Station,
                    Kind = j.Kind,
                    State = j.State,
                    CreatedAt = j.CreatedAt,
                    DeliveredAt = j.DeliveredAt,
                    FallbackTaken = j.FallbackTakenAt != null,
                };
}

/// <summary>One entry of <c>Printing/Jobs</c>.</summary>
public sealed class PrintJobEntry
{
    public string? Printer { get; set; }
    public string? Adapter { get; set; }
    public string? Agent { get; set; }
    public string? Site { get; set; }
    public string? Station { get; set; }
    public string? Kind { get; set; }
    public PrintJobState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public bool FallbackTaken { get; set; }
}
