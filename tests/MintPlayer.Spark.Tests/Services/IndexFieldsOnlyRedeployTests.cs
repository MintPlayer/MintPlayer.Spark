using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// What RavenDB does when a deployed static index is redeployed with <b>only its
/// <see cref="IndexDefinition.Fields"/> changed</b> — same Maps, same Reduce.
/// </summary>
/// <remarks>
/// Measured behaviour, pinned because a migration decision rests on it and nothing else in the
/// suite states it. The answer is <b>side by side</b>: RavenDB builds a
/// <c>ReplacementOf/{index}</c> alongside the live one and swaps only when it has caught up, so the
/// original keeps answering with a complete, non-stale result set for the whole rebuild — and keeps
/// indexing new writes while it does. A <c>Fields</c>-only change therefore costs indexing work,
/// never partial results.
/// <para>
/// The window is held open deliberately with <see cref="StopIndexingOperation"/> rather than by
/// seeding enough documents to make the rebuild slow. Racing a rebuild would make the assertions
/// pass or fail on machine speed, and — worse — pass <em>vacuously</em> on a fast one, since "no
/// replacement was ever observed" is indistinguishable from "no replacement was ever created".
/// </para>
/// <para>
/// ⚠️ These tests assert RavenDB's behaviour, not Spark's. If a future RavenDB resets in place
/// instead, they should fail — and the right response is to re-plan the index migration, not to
/// relax the assertion. <c>RavenIndexingExtensions</c> also depends on the
/// <c>ReplacementOf/</c> prefix asserted here.
/// </para>
/// </remarks>
public class IndexFieldsOnlyRedeployTests : SparkTestDriver
{
    private const int SeedCount = 500;
    private const string ReplacementPrefix = "ReplacementOf/";

    public class RedeployProbe
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Vault { get; set; } = string.Empty;
    }

    /// <summary>Distinctive name: an index class name colliding inside this assembly breaks every test in it.</summary>
    public class RedeployProbes_Overview : AbstractIndexCreationTask<RedeployProbe>
    {
        public override string IndexName => "RedeployProbes/Overview";

        public RedeployProbes_Overview()
        {
            Map = probes => from p in probes select new { p.Label, p.Vault };
        }
    }

    /// <summary>
    /// Identical to <see cref="RedeployProbes_Overview"/> but for one line — the exact line the
    /// index generator emits into a partial index, and the change the migration would make.
    /// </summary>
    public class RedeployProbes_Overview_WithSearch : AbstractIndexCreationTask<RedeployProbe>
    {
        public override string IndexName => "RedeployProbes/Overview";

        public RedeployProbes_Overview_WithSearch()
        {
            Map = probes => from p in probes select new { p.Label, p.Vault };
            Index(nameof(RedeployProbe.Vault), FieldIndexing.Search);
        }
    }

    private string IndexName => new RedeployProbes_Overview().IndexName;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new RedeployProbes_Overview().ExecuteAsync(Store);

        await using (var bulk = Store.BulkInsert())
        {
            for (var i = 0; i < SeedCount; i++)
                await bulk.StoreAsync(new RedeployProbe
                {
                    Label = $"label-{i}",
                    Vault = $"vault {i % 7} free text that needs analysing",
                });
        }

        await Store.WaitForIndexingAsync(expectedIndexes: [IndexName]);
    }

    private async Task<string[]> IndexNamesAsync()
        => await Store.Maintenance.SendAsync(new GetIndexNamesOperation(0, 128));

    private async Task<(long Total, bool Stale)> QueryAsync()
    {
        using var session = Store.OpenAsyncSession();
        // Deliberately no WaitForNonStaleResults: the question is what a live caller sees.
        await session.Query<RedeployProbe>(IndexName).Statistics(out QueryStatistics stats).Take(1).ToListAsync();
        return (stats.TotalResults, stats.IsStale);
    }

    [Fact]
    public async Task A_fields_only_change_is_rebuilt_side_by_side_and_never_serves_partial_results()
    {
        var before = await Store.Maintenance.SendAsync(new GetIndexStatisticsOperation(IndexName));
        before.EntriesCount.Should().Be(SeedCount, "the fixture must start from a fully built index, or the rest proves nothing");

        // Freeze indexing so the replacement cannot finish and swap before the assertions run.
        await Store.Maintenance.SendAsync(new StopIndexingOperation());
        try
        {
            await new RedeployProbes_Overview_WithSearch().ExecuteAsync(Store);

            (await IndexNamesAsync()).Should().Contain(ReplacementPrefix + IndexName,
                "a Fields-only change is rebuilt side by side under a ReplacementOf/ name rather than resetting the live index in place");

            var during = await Store.Maintenance.SendAsync(new GetIndexStatisticsOperation(IndexName));
            during.EntriesCount.Should().Be(SeedCount, "the live index keeps every entry while its replacement builds");

            var (total, stale) = await QueryAsync();
            total.Should().Be(SeedCount, "queries keep resolving against the old definition, so they never see a half-built index");
            stale.Should().BeFalse("the live index is not made stale by its replacement being rebuilt");
        }
        finally
        {
            await Store.Maintenance.SendAsync(new StartIndexingOperation());
        }

        await Store.WaitForIndexingAsync(expectedIndexes: [IndexName]);

        (await IndexNamesAsync()).Should().NotContain(ReplacementPrefix + IndexName, "the replacement is removed by the swap");

        var deployed = await Store.Maintenance.SendAsync(new GetIndexOperation(IndexName));
        deployed.Fields[nameof(RedeployProbe.Vault)].Indexing.Should().Be(FieldIndexing.Search,
            "the swap is what makes the new field options live");

        using var session = Store.OpenAsyncSession();
        var hits = await session.Query<RedeployProbe>(IndexName).Search(x => x.Vault, "analysing").CountAsync();
        hits.Should().Be(SeedCount, "the control: without the swap having taken effect, search() over an unanalysed field finds nothing");
    }

    [Fact]
    public async Task An_all_default_field_entry_is_normalised_away_and_costs_no_rebuild()
    {
        // The server stores Fields as an override table (docs/spark_index_agreement_PRD.md §2). An
        // entry that overrides nothing is dropped, so declaring FieldIndexing.Default explicitly is
        // free — worth pinning, because IndexDefinition.Compare() reports it as a Fields difference
        // and would predict a rebuild that does not happen.
        var deployed = await Store.Maintenance.SendAsync(new GetIndexOperation(IndexName));
        var candidate = new IndexDefinition
        {
            Name = IndexName,
            Maps = deployed.Maps,
            Fields = new() { [nameof(RedeployProbe.Label)] = new IndexFieldOptions { Indexing = FieldIndexing.Default } },
        };

        deployed.Compare(candidate).Should().Be(IndexDefinitionCompareDifferences.Fields,
            "the client compares the dictionaries structurally and cannot see the server's normalisation");

        await Store.Maintenance.SendAsync(new StopIndexingOperation());
        try
        {
            await Store.Maintenance.SendAsync(new PutIndexesOperation(candidate));

            (await IndexNamesAsync()).Should().NotContain(ReplacementPrefix + IndexName,
                "an override that overrides nothing is discarded server-side, so no rebuild is scheduled");

            (await Store.Maintenance.SendAsync(new GetIndexOperation(IndexName)))
                .Fields.Should().NotContainKey(nameof(RedeployProbe.Label),
                    "the entry is not merely ignored for rebuild purposes, it is not stored at all");
        }
        finally
        {
            await Store.Maintenance.SendAsync(new StartIndexingOperation());
        }
    }
}
