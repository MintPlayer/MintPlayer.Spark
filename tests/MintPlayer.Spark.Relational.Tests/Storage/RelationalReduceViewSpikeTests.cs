using System.Diagnostics;
using System.Linq.Expressions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Abstractions.Views;
using MintPlayer.Spark.Relational.Tests._Infrastructure;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Relational.Tests.Storage;

public class SpEvent
{
    public string? Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Day { get; set; } = string.Empty;
    public int Points { get; set; }
    public bool Credited { get; set; }
}

public class SpCell
{
    public string UserId { get; set; } = string.Empty;
    public string Day { get; set; } = string.Empty;
    public int Points { get; set; }
    public int Entries { get; set; }
}

public sealed class SpCells : SparkView<SpCell>
{
    public override Expression<Func<ISparkViewSource, IQueryable<SpCell>>> Map => s =>
        from e in s.Set<SpEvent>()
        where e.Credited == true
        select new SpCell { UserId = e.UserId, Day = e.Day, Points = e.Points, Entries = 1 };

    public override Expression<Func<IEnumerable<SpCell>, IEnumerable<SpCell>>> Reduce => rows =>
        from r in rows
        group r by new { r.UserId, r.Day } into g
        select new SpCell { UserId = g.Key.UserId, Day = g.Key.Day, Points = g.Sum(x => x.Points), Entries = g.Sum(x => x.Entries) };
}

/// <summary>
/// Spike S16-V (#536, 2026-11-25): is a <c>GROUP BY</c> view, computed on read, fast enough for moderation's map-reduce
/// indexes, or does SQL need the aggregate tables the PRD sketched? 100,000 events over 1,000 users and 30 days; one
/// user's cells and the whole view are timed. Runs only with <c>SPARK_S16_SPIKE=1</c>.
/// </summary>
public abstract class RelationalReduceViewSpikeTests(ITestOutputHelper output) : IAsyncLifetime
{
    private RelationalTestDatabase? database;

    protected abstract RelationalTestServer? Server { get; }

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("SPARK_S16_SPIKE") == "1" && Server is { } server)
            database = await RelationalTestDatabase.CreateAsync(server, [typeof(SpEvent)], views: [new SpCells()], database: "spark_s16_" + Guid.NewGuid().ToString("N")[..12]);
    }

    public async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }

    [SkippableFact]
    public async Task A_group_by_view_over_100k_rows()
    {
        Skip.If(database is null, "SPARK_S16_SPIKE=1 and a server are needed.");
        var store = database!.Store;

        var seed = Stopwatch.StartNew();
        for (var batch = 0; batch < 100; batch++)
        {
            using var session = store.OpenSession();
            for (var i = 0; i < 1000; i++)
            {
                var n = batch * 1000 + i;
                await session.StoreAsync(new SpEvent { Id = $"spevents/{n}", UserId = $"u{n % 1000}", Day = $"d{n % 30}", Points = 1 + n % 5, Credited = n % 4 != 0 });
            }
            await session.SaveChangesAsync();
        }
        output.WriteLine($"seed 100k: {seed.ElapsedMilliseconds} ms");

        for (var run = 0; run < 3; run++)
        {
            using var session = store.OpenSession();
            var one = Stopwatch.StartNew();
            var cells = await session.ToListAsync(session.Query<SpCell, SpCells>().Where(c => c.UserId == "u42"));
            one.Stop();
            var all = Stopwatch.StartNew();
            var total = await session.ToListAsync(session.Query<SpCell, SpCells>());
            all.Stop();
            output.WriteLine($"run {run}: one user {cells.Count} cells in {one.ElapsedMilliseconds} ms; whole view {total.Count} cells in {all.ElapsedMilliseconds} ms");
        }
    }
}

public sealed class SqlServerReduceViewSpikeTests(ITestOutputHelper output) : RelationalReduceViewSpikeTests(output)
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlReduceViewSpikeTests(ITestOutputHelper output) : RelationalReduceViewSpikeTests(output)
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}
