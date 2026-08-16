using System.Diagnostics;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using RavenTimeoutException = Raven.Client.Exceptions.RavenTimeoutException;

namespace MintPlayer.Spark.Tests._Infrastructure;

/// <summary>
/// Pins what an index wait actually promises, because the intuitive reading is wrong in a way that
/// matters: on a database with no indexes yet — which is every fixture's starting point, since each
/// test gets its own — a "wait for all indexes to be non-stale" waits for <b>nothing</b>. The
/// condition is universally quantified over the index set, so an empty set satisfies it instantly.
/// <para>
/// What actually makes the common seed-then-query pattern correct is RavenDB itself: the query
/// creates the auto-index it needs and blocks on that first creation. Knowing which of the two
/// mechanisms is carrying a test matters the moment someone changes one of them.
/// </para>
/// </summary>
public class IndexWaitSemanticsTests : SparkTestDriver
{
    public class Thing
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    [Fact]
    public async Task Waiting_without_expected_indexes_is_vacuous_on_an_empty_database()
    {
        (await Store.Maintenance.SendAsync(new GetStatisticsOperation())).Indexes
            .Should().BeEmpty("a fresh per-test database starts with no indexes");

        await SeedAsync(session => session.StoreAsync(new Thing { Name = "alpha" }));

        var sw = Stopwatch.StartNew();
        await Store.WaitForIndexingAsync();
        sw.Stop();

        // All() over an empty sequence is true, so there is nothing to block on. Correct, but not
        // the guarantee the name suggests — which is exactly why expectedIndexes exists.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "with no indexes the staleness condition is vacuously satisfied");

        (await Store.Maintenance.SendAsync(new GetStatisticsOperation())).Indexes
            .Should().BeEmpty("neither the seed nor the wait creates an index");
    }

    [Fact]
    public async Task Naming_an_expected_index_turns_the_vacuous_pass_into_a_real_failure()
    {
        // The whole point of the deployed-AND-up-to-date pairing: a wait for an index that was
        // never deployed must fail loudly, not sail through because the database happens to be
        // empty. Without this, a missing index registration surfaces much later as a query
        // returning no rows.
        var act = async () => await Store.WaitForIndexingAsync(
            timeout: TimeSpan.FromMilliseconds(300),
            expectedIndexes: ["Things/ByNameThatWasNeverDeployed"]);

        // Not a TimeoutException: waiting longer would not help. The type carries the distinction
        // between "indexing was slow" and "this index is broken", so a red build says which.
        var thrown = await act.Should().ThrowAsync<RavenIndexDeploymentException>();

        thrown.Which.MissingIndexes.Should().ContainSingle()
            .Which.Should().Be("Things/ByNameThatWasNeverDeployed");
        thrown.Which.FaultedIndexes.Should().BeEmpty();
        thrown.Which.Message.Should().Contain("never deployed");
    }

    [Fact]
    public async Task A_stale_but_healthy_index_still_reports_a_timeout()
    {
        // The other side of the same distinction: nothing is broken here, indexing is simply
        // stopped — so this must stay a TimeoutException.
        await Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.StopIndexingOperation());
        try
        {
            await SeedAsync(session => session.StoreAsync(new Thing { Name = "beta" }));
        }
        catch (RavenTimeoutException)
        {
            // Expected: with indexing stopped the server-side wait on the write cannot complete.
        }

        try
        {
            using var session = Store.OpenAsyncSession();
            _ = await session.Query<Thing>().Where(t => t.Name == "beta").ToListAsync();

            var act = async () => await Store.WaitForIndexingAsync(timeout: TimeSpan.FromMilliseconds(300));

            await act.Should().ThrowAsync<TimeoutException>()
                .Where(e => !(e is RavenIndexDeploymentException),
                    "a healthy-but-stale index is a timeout, not a deployment failure");
        }
        finally
        {
            await Store.Maintenance.SendAsync(new Raven.Client.Documents.Operations.Indexes.StartIndexingOperation());
        }
    }

    [Fact]
    public async Task The_query_creates_the_auto_index_and_blocks_on_it_which_is_what_makes_seed_then_query_safe()
    {
        await SeedAsync(session => session.StoreAsync(new Thing { Name = "alpha" }));
        await Store.WaitForIndexingAsync();

        using (var session = Store.OpenAsyncSession())
        {
            var hits = await session.Query<Thing>().Where(t => t.Name == "alpha").ToListAsync();

            hits.Should().ContainSingle(
                "RavenDB creates the auto-index for this query and waits for its first build — "
                + "this, not our index wait, is what protects seed-then-query on a fresh database");
        }

        (await Store.Maintenance.SendAsync(new GetStatisticsOperation())).Indexes
            .Select(i => i.Name)
            .Should().ContainSingle(n => n.StartsWith("Auto/Things", StringComparison.Ordinal),
                "the query is what brought the index into existence");
    }
}
