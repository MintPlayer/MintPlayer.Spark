using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Issue #431 — a column's distinct-value list is a disclosure surface and is gated like one.
/// </summary>
/// <remarks>
/// Listing a column's values is strictly stronger than filtering by a value already known: someone
/// who holds an account number may legitimately filter by it while having no business reading every
/// account number in the system. That is why <c>canListDistincts</c> is separate from
/// <c>canFilter</c>, and it is what these tests pin.
/// <para>
/// The sharpest assertion here is the blunt one — under <see cref="DenyAllRowSecurity"/> the list
/// must be <b>empty</b>. This feature's tempting implementation is a RavenDB facet, which aggregates
/// in the database, where the row filter frequently is not: it refuses to compose into a projection
/// query, and the gate that filters those runs after materialization. A facet would therefore publish
/// values drawn from rows the caller cannot read, and no amount of endpoint-level checking would
/// notice. A test that only ever asserts on a permissive caller cannot tell the two implementations
/// apart.
/// </para>
/// <para>
/// The second rule, easy to lose in a refactor: <b>refused and empty are the same answer</b>. A
/// caller must not be able to distinguish "you may not list this column" from "this column has no
/// values", because the first is a fact about their rights.
/// </para>
/// </remarks>
public class DistinctValuesDisclosureTests : SparkTestDriver
{
    private static readonly Guid DistinctVaultTypeId = Guid.Parse("dddd4444-dddd-dddd-dddd-dddd44444444");

    public class DistinctVault
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;

        /// <summary>Modelled but kept off the grid — the shape an app uses to hide an attribute.</summary>
        public string SecretToken { get; set; } = string.Empty;
    }

    public class DistinctVaults_Overview : AbstractIndexCreationTask<DistinctVault>
    {
        public DistinctVaults_Overview()
        {
            Map = vaults => from v in vaults select new { v.Label, v.Region, v.SecretToken };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<DistinctVault> Vaults => Session.Query<DistinctVault>();
    }

    private static EntityTypeFile DistinctVaultModel(
        bool? labelCanListDistincts = null,
        bool? regionCanFilter = null) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = DistinctVaultTypeId,
            Name = "DistinctVault",
            ClrType = typeof(DistinctVault).FullName!,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = nameof(DistinctVault.Label), DataType = "string",
                    CanListDistincts = labelCanListDistincts,
                },
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = nameof(DistinctVault.Region), DataType = "string",
                    CanFilter = regionCanFilter,
                },
                // Off the query surface entirely: neither filterable nor listable, and not because
                // of a capability flag.
                new EntityAttributeDefinition
                {
                    Id = Guid.NewGuid(), Name = nameof(DistinctVault.SecretToken), DataType = "string",
                    ShowedOn = EShowedOn.PersistentObject,
                },
            ],
        },
    };

    private SparkEndpointFactory<TestContext>? _factory;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new DistinctVaults_Overview().ExecuteAsync(Store);

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new DistinctVault { Label = "beta", Region = "eu", SecretToken = "aaa" });
            await session.StoreAsync(new DistinctVault { Label = "alpha", Region = "eu", SecretToken = "zzz" });
            await session.StoreAsync(new DistinctVault { Label = "gamma", Region = "us", SecretToken = "mmm" });
            // A duplicate Label, so de-duplication is actually exercised rather than assumed.
            await session.StoreAsync(new DistinctVault { Label = "alpha", Region = "ap", SecretToken = "qqq" });
        });
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private IQueryExecutor Executor(EntityTypeFile? model = null, IRowSecurity? rowSecurity = null)
    {
        _factory = new SparkEndpointFactory<TestContext>(
            Store,
            [model ?? DistinctVaultModel()],
            configureServices: rowSecurity is null
                ? null
                : services => services.AddSingleton(rowSecurity),
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(DistinctVaults_Overview)));

        return _factory.GetService<IQueryExecutor>();
    }

    private static SparkQuery Query(params SparkQueryColumn[] columns) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Vaults",
        Source = "Database.Vaults",
        Columns = columns,
    };

    [Fact]
    public async Task Values_the_caller_may_not_read_are_never_listed()
    {
        var executor = Executor(rowSecurity: new DenyAllRowSecurity());

        var result = await executor.GetDistinctValuesAsync(Query(), nameof(DistinctVault.Label));

        result.Matching.Should().BeEmpty(
            "row security refused every row, so there is nothing whose values may be published — "
            + "an implementation that aggregates in the database would still list all four");
        result.HasMore.Should().BeFalse("an empty list is not a truncated one");
    }

    [Fact]
    public async Task A_column_off_the_query_surface_yields_nothing()
    {
        var executor = Executor();

        var result = await executor.GetDistinctValuesAsync(Query(), nameof(DistinctVault.SecretToken));

        result.Matching.Should().BeEmpty(
            "ShowedOn is the authorization boundary and it is checked before any capability flag");
    }

    [Fact]
    public async Task A_column_that_may_not_be_listed_is_indistinguishable_from_an_empty_one()
    {
        var executor = Executor(DistinctVaultModel(labelCanListDistincts: false));

        var result = await executor.GetDistinctValuesAsync(Query(), nameof(DistinctVault.Label));

        result.Matching.Should().BeEmpty("canListDistincts:false withholds the values");
        result.HasMore.Should().BeFalse(
            "the refusal must not differ from an empty result in any observable way, or it answers "
            + "the question it exists to refuse");
    }

    [Fact]
    public async Task A_query_override_can_close_a_column_the_attribute_leaves_open()
    {
        var executor = Executor();

        var open = await executor.GetDistinctValuesAsync(Query(), nameof(DistinctVault.Label));
        open.Matching.Should().NotBeEmpty("the attribute states nothing, so the default is capable");

        var closed = await executor.GetDistinctValuesAsync(
            Query(new SparkQueryColumn { Name = nameof(DistinctVault.Label), CanListDistincts = false }),
            nameof(DistinctVault.Label));

        closed.Matching.Should().BeEmpty("the query's override narrows what the attribute left open");
    }

    [Fact]
    public async Task Values_are_deduplicated_by_value_and_sorted_by_label()
    {
        var executor = Executor();

        var result = await executor.GetDistinctValuesAsync(Query(), nameof(DistinctVault.Label));

        result.Matching.Select(v => v.Label).Should().Equal(
            ["alpha", "beta", "gamma"],
            "four rows carry three distinct labels, and the reader scans the label");
    }

    [Fact]
    public async Task A_search_term_narrows_the_list()
    {
        var executor = Executor();

        var result = await executor.GetDistinctValuesAsync(Query(), nameof(DistinctVault.Label), search: "al");

        result.Matching.Select(v => v.Label).Should().Equal(["alpha"]);
    }

    [Fact]
    public async Task Another_columns_filter_narrows_which_values_remain_reachable()
    {
        var executor = Executor();

        var result = await executor.GetDistinctValuesAsync(
            Query(), nameof(DistinctVault.Label),
            columnFilters: [new QueryColumnFilter { Name = nameof(DistinctVault.Region), Includes = ["us"] }]);

        result.Matching.Select(v => v.Label).Should().Equal(
            ["gamma"],
            "offering a value that yields an empty grid the moment it is picked is worse than omitting it");
    }
}
