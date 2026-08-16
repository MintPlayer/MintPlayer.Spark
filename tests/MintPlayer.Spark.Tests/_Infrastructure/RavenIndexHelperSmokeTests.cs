using System.Reflection;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Exercises <see cref="SparkTestDriver.IndexAssemblies"/> + <see cref="RavenIndexHelper"/> so a
/// subclass that declares an index gets it deployed and indexed before the first <c>[Fact]</c>
/// runs. A bug in the index-wait plumbing surfaces here as a stale-query failure instead of
/// mysteriously flaky behaviour inside a real test.
/// </summary>
public class RavenIndexHelperSmokeTests : SparkTestDriver
{
    public class Widget
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Weight { get; set; }
    }

    public class Widgets_ByName : AbstractIndexCreationTask<Widget>
    {
        public Widgets_ByName()
        {
            Map = widgets => from w in widgets select new { w.Name };
        }
    }

    protected override IEnumerable<Assembly> IndexAssemblies => new[] { typeof(RavenIndexHelperSmokeTests).Assembly };

    [Fact]
    public async Task Declared_index_is_live_and_queryable_without_manual_WaitForIndexing()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Widget { Name = "left", Weight = 1 });
            await session.StoreAsync(new Widget { Name = "right", Weight = 2 });
            await session.SaveChangesAsync();
        }

        // After SaveChanges the new docs are stale against Widgets_ByName — explicitly wait,
        // then query WITHOUT a WaitForNonStaleResults hint. A green assertion here proves the
        // helper did its job (otherwise RavenDB returns zero hits from the stale index).
        await RavenIndexHelper.WaitForNonStaleAsync(Store);

        using var readSession = Store.OpenAsyncSession();
        var hits = await readSession.Query<Widget, Widgets_ByName>()
            .Where(w => w.Name == "left")
            .ToListAsync();

        hits.Should().HaveCount(1);
        hits[0].Weight.Should().Be(1);
    }

    [Fact]
    public async Task Both_entry_points_settle_the_same_database()
    {
        // #256 — these were two separate loops with different done-conditions, different exception
        // types and their own timeouts, so which one a fixture happened to call changed what
        // "settled" meant. They are now one implementation; this pins that they agree.
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new Widget { Name = "consolidated", Weight = 7 });
            await session.SaveChangesAsync();
        }

        await RavenIndexHelper.WaitForNonStaleAsync(Store);
        await Store.WaitForIndexingAsync();
        Store.WaitForIndexing();

        using var readSession = Store.OpenAsyncSession();
        var hits = await readSession.Query<Widget, Widgets_ByName>()
            .Where(w => w.Name == "consolidated")
            .ToListAsync();

        hits.Should().HaveCount(1);
    }

    [Fact]
    public async Task Timeout_message_names_the_database_and_the_elapsed_wait()
    {
        // A wait that fails must say what it was waiting on. The old sync path reported only
        // "the indexes stayed stale", which tells the reader nothing actionable.
        // Stop indexing FIRST, then write — otherwise the document is indexed before the freeze
        // and there is nothing stale to wait on. (Maintenance operations are database-scoped, and
        // SparkTestDriver gives every test its own database, so this cannot affect another test.)
        await Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.StopIndexingOperation());
        try
        {
            using (var session = Store.OpenAsyncSession())
            {
                await session.StoreAsync(new Widget { Name = "slow", Weight = 3 });
                await session.SaveChangesAsync();
            }

            var act = async () => await Store.WaitForIndexingAsync(timeout: TimeSpan.FromMilliseconds(200));

            (await act.Should().ThrowAsync<TimeoutException>())
                .Which.Message.Should().Contain(Store.Database).And.Contain("stale");
        }
        finally
        {
            await Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.StartIndexingOperation());
        }
    }
}
