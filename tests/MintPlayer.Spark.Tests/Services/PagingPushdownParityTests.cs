using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Issue #431 M14 — pushing <c>Skip</c>/<c>Take</c> into the database must be invisible to the caller.
/// </summary>
/// <remarks>
/// Two execution paths now exist for the same query, chosen per request, and the whole point is that
/// only the cost differs. A performance change that quietly returns different rows or a different
/// count is a correctness bug wearing a performance fix's clothes, and it would show up as "the grid
/// says 40 items but page 3 is empty" long after the change that caused it.
/// <para>
/// <b><c>TotalItems</c> is the dangerous half.</b> Taking it from the database is only sound where
/// nothing removes rows afterwards; anywhere else it counts rows the caller may not see, which is
/// precisely the cardinality oracle this codebase already refused once on an author-supplied total.
/// </para>
/// </remarks>
public class PagingPushdownParityTests : SparkTestDriver
{
    private static readonly Guid PagedLedgerTypeId = Guid.Parse("bbbb2222-bbbb-bbbb-bbbb-bbbb22222222");

    public class PagedLedger
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public int Sequence { get; set; }
    }

    public class PagedLedgers_Overview : AbstractIndexCreationTask<PagedLedger>
    {
        public PagedLedgers_Overview()
        {
            Map = ledgers => from l in ledgers select new { l.Label, l.Sequence };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<PagedLedger> PagedLedgers => Session.Query<PagedLedger>();
    }

    private static EntityTypeFile PagedLedgerModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = PagedLedgerTypeId,
            Name = "PagedLedger",
            ClrType = typeof(PagedLedger).FullName!,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(PagedLedger.Label), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(PagedLedger.Sequence), DataType = "number" },
            ],
        },
    };

    private SparkEndpointFactory<TestContext>? _factory;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new PagedLedgers_Overview().ExecuteAsync(Store);

        await SeedAsync(async session =>
        {
            for (var i = 0; i < 25; i++)
                await session.StoreAsync(new PagedLedger { Label = $"row-{i:D2}", Sequence = i });
        });
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private IQueryExecutor Executor(IRowSecurity? rowSecurity = null)
    {
        _factory = new SparkEndpointFactory<TestContext>(
            Store, [PagedLedgerModel()],
            configureServices: rowSecurity is null
                ? null
                : services => services.AddSingleton(rowSecurity),
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(PagedLedgers_Overview)));

        return _factory.GetService<IQueryExecutor>();
    }

    private static SparkQuery Query() => new()
    {
        Id = Guid.NewGuid(),
        Name = "PagedLedgers",
        Source = "Database.PagedLedgers",
        SortColumns = [new SortColumn { Property = nameof(PagedLedger.Sequence), Direction = "asc" }],
    };

    [Fact]
    public async Task A_page_is_the_same_page_whichever_path_produced_it()
    {
        // PermissiveRowSecurity reports NoRule with no per-row refinement, so paging pushes down.
        var pushdown = await Executor(new _Infrastructure.PermissiveRowSecurity())
            .ExecuteQueryAsync(Query(), skip: 10, take: 5);

        await _factory!.DisposeAsync();
        _factory = null;

        // DenyAllRowSecurity would return nothing, so the in-memory path needs a rule that keeps
        // rows while still refusing pushdown — the real executor's own default does exactly that
        // when no double is injected.
        var inMemory = await Executor().ExecuteQueryAsync(Query(), skip: 10, take: 5);

        pushdown.Items.Select(i => i.Id).Should().Equal(
            inMemory.Items.Select(i => i.Id),
            "the same query, data and caller must yield the same page on either path");
        pushdown.TotalItems.Should().Be(inMemory.TotalItems,
            "and the same total — a count that differs by path is the bug this test exists for");
    }

    [Fact]
    public async Task The_total_counts_matches_not_the_page()
    {
        var result = await Executor(new _Infrastructure.PermissiveRowSecurity())
            .ExecuteQueryAsync(Query(), skip: 0, take: 5);

        result.Items.Should().HaveCount(5, "the page is what was asked for");
        result.TotalItems.Should().Be(25,
            "the total describes the result set, not the page — reading it off the rows in hand is "
            + "the mistake pushdown makes easy");
    }

    [Fact]
    public async Task Paging_past_the_end_yields_an_empty_page_and_an_intact_total()
    {
        var result = await Executor(new _Infrastructure.PermissiveRowSecurity())
            .ExecuteQueryAsync(Query(), skip: 100, take: 5);

        result.Items.Should().BeEmpty();
        result.TotalItems.Should().Be(25, "an empty page does not mean an empty result set");
    }

    [Fact]
    public async Task A_row_rule_that_filters_after_materialization_refuses_pushdown()
    {
        // The rows are gone by the time the caller sees them either way; what this pins is that the
        // count is not taken from the database on a path where rows disappear afterwards.
        var result = await Executor(new _Infrastructure.DenyAllRowSecurity())
            .ExecuteQueryAsync(Query(), skip: 0, take: 5);

        result.Items.Should().BeEmpty();
        result.TotalItems.Should().Be(0,
            "counting in the database here would report 25 matches for a caller who may see none — "
            + "the cardinality oracle, restated");
    }
}
