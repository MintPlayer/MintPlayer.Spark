using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// What RavenDB does with a field the index maps but declares <see cref="FieldIndexing.No"/> — and
/// the point is that it does it <b>silently</b>.
/// </summary>
/// <remarks>
/// Measured behaviour, pinned because the framework's decisions rest on it and nothing else in the
/// suite states it. A field <em>absent from the Map</em> throws, loudly, and that is the failure this
/// PR fixed (<c>StaticIndexSearchTests</c>). A field present but not indexed is worse: a filter
/// returns zero rows and a sort does nothing, both with HTTP 200 and no diagnostic anywhere.
/// <para>
/// This is why the design refuses to translate a <c>canSort: false</c> model flag into
/// <c>FieldIndexing.No</c> — see <c>docs/subquery_column_filters_PRD.md</c> §3.10.3. Doing so would
/// turn a presentation judgement into an invisible data-correctness bug, and would also apply to
/// every query through that index rather than the one whose model asked for it.
/// </para>
/// <para>
/// ⚠️ These tests assert a RavenDB behaviour, not Spark's. If a future RavenDB starts erroring
/// instead of degrading, they should fail — and the right response is to read the new behaviour and
/// revisit the decision above, not to relax the assertion.
/// </para>
/// </remarks>
public class UnindexedFieldDegradationTests : SparkTestDriver
{
    private static readonly Guid CrateTypeId = Guid.Parse("dddd4444-dddd-dddd-dddd-dddd44444444");

    public class DegradationLedger
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;

        /// <summary>Mapped by the index, but declared <see cref="FieldIndexing.No"/>.</summary>
        public string Vault { get; set; } = string.Empty;
    }

    /// <summary>Distinctive name: an index class name colliding inside this assembly breaks every test in it.</summary>
    public class DegradationLedgers_Overview : AbstractIndexCreationTask<DegradationLedger>
    {
        public DegradationLedgers_Overview()
        {
            Map = ledgers => from l in ledgers select new { l.Label, l.Vault };

            // The whole subject. Vault IS in the map, so it is not the "field is not indexed" error;
            // it is simply not searchable, filterable or sortable, and RavenDB says nothing about it.
            Index(nameof(DegradationLedger.Vault), FieldIndexing.No);
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class LedgerContext : SparkContext
    {
        public IRavenQueryable<DegradationLedger> Ledgers => Session.Query<DegradationLedger, DegradationLedgers_Overview>();
    }

    private static EntityTypeFile LedgerModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CrateTypeId,
            Name = "DegradationLedger",
            ClrType = typeof(DegradationLedger).FullName!,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(DegradationLedger.Label), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(DegradationLedger.Vault), DataType = "string" },
            ],
        },
    };

    private SparkEndpointFactory<LedgerContext>? _factory;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new DegradationLedgers_Overview().ExecuteAsync(Store);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new DegradationLedger { Label = "alpha", Vault = "north" });
            await session.StoreAsync(new DegradationLedger { Label = "beta", Vault = "south" });
            await session.StoreAsync(new DegradationLedger { Label = "gamma", Vault = "north" });
        });
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private IQueryExecutor Executor()
    {
        _factory = new SparkEndpointFactory<LedgerContext>(
            Store, [LedgerModel()],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(DegradationLedgers_Overview)));

        return _factory.GetService<IQueryExecutor>();
    }

    private static SparkQuery Query() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Ledgers",
        Source = "Database.Ledgers",
    };

    [Fact]
    public async Task A_filter_on_an_unindexed_field_returns_no_rows_and_no_error()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(DegradationLedger.Vault), Includes = ["north"] }]);

        // Two rows genuinely have Vault == "north". The filter still matches nothing, because the
        // field carries no index terms — and the caller is told 200 with an empty grid, which is
        // indistinguishable from "no rows match". That indistinguishability is the hazard.
        result.TotalItems.Should().Be(0,
            "FieldIndexing.No makes a filter match nothing rather than fail, so a mis-declared index "
            + "reads to a user as an empty result rather than as a bug");
    }

    [Fact]
    public async Task A_filter_on_an_INDEXED_field_of_the_same_index_still_works()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(Query(),
            columnFilters: [new QueryColumnFilter { Name = nameof(DegradationLedger.Label), Includes = ["alpha"] }]);

        // The control. Without it the test above would also pass if filtering were broken outright,
        // or if the index had failed to deploy — an assertion that everything returns nothing proves
        // nothing.
        result.TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task Sorting_by_an_unindexed_field_is_a_silent_no_op()
    {
        var executor = Executor();

        var ascending = await executor.ExecuteQueryAsync(
            Query().WithSortColumns([new SortColumn { Property = nameof(DegradationLedger.Vault), Direction = "asc" }]));
        var descending = await executor.ExecuteQueryAsync(
            Query().WithSortColumns([new SortColumn { Property = nameof(DegradationLedger.Vault), Direction = "desc" }]));

        // Asserted as "ascending and descending agree" rather than against a fixed order: the claim
        // is that the sort had NO effect, and two opposite sorts returning identical order is what
        // that means. Pinning one literal order would pass just as well if the rows happened to
        // arrive that way.
        ascending.Items.Select(i => i.Breadcrumb).Should().Equal(
            descending.Items.Select(i => i.Breadcrumb),
            "a sort on a field with no index terms reorders nothing, and reports no error either way");
    }
}
