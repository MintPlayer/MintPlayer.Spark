using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Abstractions.Views;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// #536 S16: a <see cref="SparkView{TRow}"/> with a <c>Reduce</c> on the database <c>SPARK_TEST_DATABASE</c> picks. RavenDB
/// deploys a map-reduce index, SQL Server and PostgreSQL a <c>GROUP BY</c> view, memory a grouping. The sums follow
/// inserts, updates that move a row in or out of the map's filter, and deletes, set-based ones included.
/// </summary>
public class NeutralReduceViewTests : SparkTestDriver
{
    public sealed class ReduceViewContext : SparkContext;

    public class RvEvent
    {
        public string? Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Day { get; set; } = string.Empty;
        public int Points { get; set; }
        public bool Credited { get; set; }
    }

    public class RvCell
    {
        public string UserId { get; set; } = string.Empty;
        public string Day { get; set; } = string.Empty;
        public int Points { get; set; }
        public int Entries { get; set; }
    }

    /// <summary>Credited points per (user, day).</summary>
    public sealed class RvCells : SparkView<RvCell>
    {
        public override Expression<Func<ISparkViewSource, IQueryable<RvCell>>> Map => s =>
            from e in s.Set<RvEvent>()
            where e.Credited == true
            select new RvCell { UserId = e.UserId, Day = e.Day, Points = e.Points, Entries = 1 };

        public override Expression<Func<IEnumerable<RvCell>, IEnumerable<RvCell>>> Reduce => rows =>
            from r in rows
            group r by new { r.UserId, r.Day } into g
            select new RvCell { UserId = g.Key.UserId, Day = g.Key.Day, Points = g.Sum(x => x.Points), Entries = g.Sum(x => x.Entries) };
    }

    public class RvTotal
    {
        public string UserId { get; set; } = string.Empty;
        public string Day { get; set; } = string.Empty;
        public int Points { get; set; }
        public int Entries { get; set; }
    }

    public class RvBonus
    {
        public string? Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Day { get; set; } = string.Empty;
        public int Amount { get; set; }
    }

    /// <summary>
    /// A multi-map with a reduce: credited events and bonuses, two collections, summed per (user, day). RavenDB's
    /// <c>AddMap</c> per collection; SQL's <c>UNION ALL</c> under the <c>GROUP BY</c>.
    /// </summary>
    public sealed class RvTotals : SparkView<RvTotal>
    {
        public override Expression<Func<ISparkViewSource, IQueryable<RvTotal>>> Map => s =>
            s.Set<RvEvent>().Where(e => e.Credited == true)
                .Select(e => new RvTotal { UserId = e.UserId, Day = e.Day, Points = e.Points, Entries = 1 })
            .Concat(s.Set<RvBonus>()
                .Select(b => new RvTotal { UserId = b.UserId, Day = b.Day, Points = b.Amount, Entries = 0 }));

        public override Expression<Func<IEnumerable<RvTotal>, IEnumerable<RvTotal>>> Reduce => rows =>
            from r in rows
            group r by new { r.UserId, r.Day } into g
            select new RvTotal { UserId = g.Key.UserId, Day = g.Key.Day, Points = g.Sum(x => x.Points), Entries = g.Sum(x => x.Entries) };
    }

    private SparkEndpointFactory<ReduceViewContext> NewHost() => new(
        Database,
        [],
        configureServices: services =>
        {
            services.AddSparkStoredType<RvEvent>();
            services.AddSparkStoredType<RvBonus>();
        },
        configureIndexCatalog: catalog =>
        {
            catalog.RegisterIndex(typeof(RvCells));
            catalog.RegisterIndex(typeof(RvTotals));
        });

    [Fact]
    public async Task A_multi_map_reduce_view_sums_rows_of_two_collections()
    {
        await using var host = NewHost();
        await StoreAsync(host,
            new RvEvent { Id = "rvevents/1", UserId = "alice", Day = "d1", Points = 2, Credited = true },
            new RvEvent { Id = "rvevents/2", UserId = "alice", Day = "d1", Points = 50, Credited = false });
        using (var session = host.GetService<ISparkStore>().OpenSession())
        {
            await session.StoreAsync(new RvBonus { Id = "rvbonus/1", UserId = "alice", Day = "d1", Amount = 10 });
            await session.StoreAsync(new RvBonus { Id = "rvbonus/2", UserId = "alice", Day = "d3", Amount = 4 });
            await session.SaveChangesAsync();
        }

        await host.WaitForConsistencyAsync();
        using var read = host.GetService<ISparkStore>().OpenSession();
        var totals = (await read.ToListAsync(read.Query<RvTotal, RvTotals>().Where(c => c.UserId == "alice"))).OrderBy(c => c.Day).ToList();

        totals.Select(c => (c.Day, c.Points, c.Entries)).Should().Equal([("d1", 12, 1), ("d3", 4, 0)]);
    }

    private static async Task<List<RvCell>> CellsAsync(SparkEndpointFactory<ReduceViewContext> host)
    {
        await host.WaitForConsistencyAsync();
        using var session = host.GetService<ISparkStore>().OpenSession();
        var cells = await session.ToListAsync(session.Query<RvCell, RvCells>().Where(c => c.UserId == "alice"));
        return [.. cells.OrderBy(c => c.Day)];
    }

    private static async Task StoreAsync(SparkEndpointFactory<ReduceViewContext> host, params RvEvent[] events)
    {
        using var session = host.GetService<ISparkStore>().OpenSession();
        foreach (var e in events)
            await session.StoreAsync(e);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task A_reduce_view_sums_the_mapped_rows_per_key_and_follows_every_write()
    {
        await using var host = NewHost();
        await StoreAsync(host,
            new RvEvent { Id = "rvevents/1", UserId = "alice", Day = "d1", Points = 2, Credited = true },
            new RvEvent { Id = "rvevents/2", UserId = "alice", Day = "d1", Points = 3, Credited = true },
            new RvEvent { Id = "rvevents/3", UserId = "alice", Day = "d2", Points = 5, Credited = true },
            new RvEvent { Id = "rvevents/4", UserId = "alice", Day = "d2", Points = 100, Credited = false },
            new RvEvent { Id = "rvevents/5", UserId = "bob", Day = "d1", Points = 7, Credited = true });

        var cells = await CellsAsync(host);
        cells.Select(c => (c.Day, c.Points, c.Entries)).Should().Equal([("d1", 5, 2), ("d2", 5, 1)]);

        // An update that moves a row into the map's filter, and a delete.
        using (var session = host.GetService<ISparkStore>().OpenSession())
        {
            (await session.LoadAsync<RvEvent>("rvevents/4"))!.Credited = true;
            session.Delete(typeof(RvEvent), "rvevents/1");
            await session.SaveChangesAsync();
        }

        cells = await CellsAsync(host);
        cells.Select(c => (c.Day, c.Points, c.Entries)).Should().Equal([("d1", 3, 1), ("d2", 105, 2)]);

        // Set-based writes move the sums too.
        using (var session = host.GetService<ISparkStore>().OpenSession())
        {
            await session.UpdateWhereAsync<RvEvent>(e => e.Day == "d2", s => s.Set(e => e.Points, 1));
            await session.DeleteWhereAsync<RvEvent>(e => e.Day == "d1");
        }

        cells = await CellsAsync(host);
        cells.Select(c => (c.Day, c.Points, c.Entries)).Should().Equal([("d2", 2, 2)]);
    }
}
