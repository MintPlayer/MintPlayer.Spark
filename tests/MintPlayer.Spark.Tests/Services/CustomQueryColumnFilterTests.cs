using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Per-column filters (#431) over a <c>Custom.*</c> source — the combination that shipped broken.
/// </summary>
/// <remarks>
/// The two existing suites are disjoint exactly here: <c>ColumnFilterDisclosureTests</c> filters over
/// <c>Database.*</c> with no parent, and <c>ExecuteQueryParentGateTests</c> runs sub-queries over
/// <c>Custom.*</c> with no filters. Nothing covered the intersection, and a declared sub-query is
/// <em>required</em> to be <c>Custom.*</c> — a <c>Database.*</c> source reads a SparkContext property
/// and cannot be scoped to a parent — so the intersection is precisely the shape every sub-query in
/// every app has.
/// <para>
/// Two things here are asserted on emitted RQL rather than on rows, because rows cannot distinguish
/// them. A filter that ran in C# over a materialized list returns exactly the same rows as one that
/// ran in the database, and a filter composed as an alternative to the row-security predicate rather
/// than as a conjunct returns exactly the right rows for a permissive caller. Counting rows would
/// pass in both cases.
/// </para>
/// </remarks>
public class CustomQueryColumnFilterTests : SparkTestDriver
{
    private static readonly Guid ParcelTypeId = Guid.Parse("bbbb7777-bbbb-bbbb-bbbb-bbbb77777777");
    private static readonly Guid SecuredParcelTypeId = Guid.Parse("bbbb8888-bbbb-bbbb-bbbb-bbbb88888888");

    public class Parcel
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string Depot { get; set; } = string.Empty;
    }

    /// <summary>Separate type so the row-rule tests do not have to fight a filter on every other test.</summary>
    public class SecuredParcel
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
    }

    public class ParcelActions : DefaultPersistentObjectActions<Parcel>
    {
        private readonly IAsyncDocumentSession _session;
        public ParcelActions(IEntityMapper entityMapper, IAsyncDocumentSession session) : base(entityMapper)
            => _session = session;

        /// <summary>The shape every real sub-query has: Raven-backed, so a filter can push down.</summary>
        public IRavenQueryable<Parcel> AllParcels(CustomQueryArgs _) => _session.Query<Parcel>();

        /// <summary>Parent-scoped, exactly as an app author writes a sub-query.</summary>
        public IRavenQueryable<Parcel> ParcelsOfDepot(CustomQueryArgs args)
        {
            ArgumentNullException.ThrowIfNull(args.Parent);
            return _session.Query<Parcel>().Where(p => p.Depot == args.Parent!.Id);
        }

        /// <summary>
        /// An <see cref="IQueryable{T}"/> whose provider is <c>EnumerableQuery</c>. The trap:
        /// <c>Queryable.Where</c> composes onto this without error and filters in process, so wiring
        /// the filter in without a provider check would silently move filtering into C#.
        /// </summary>
        public IQueryable<Parcel> InMemoryParcels(CustomQueryArgs _) => new[]
        {
            new Parcel { Id = "memory/1", Label = "mem-one", Region = "eu", Depot = "depots/1" },
            new Parcel { Id = "memory/2", Label = "mem-two", Region = "us", Depot = "depots/1" },
        }.AsQueryable();

        /// <summary>
        /// A fixed set that is not an <see cref="IQueryable"/> at all — a computed dashboard, a
        /// constant list, an API response. There is no provider to compose onto, so the filter is
        /// lifted through <c>AsQueryable</c> rather than reimplemented.
        /// </summary>
        public IEnumerable<Parcel> ListParcels(CustomQueryArgs _) =>
        [
            new Parcel { Id = "list/1", Label = "list-one", Region = "eu", Depot = "depots/3" },
            new Parcel { Id = "list/2", Label = "list-two", Region = "us", Depot = "depots/3" },
        ];

        /// <summary>
        /// Goes to the database and then materializes — a developer deciding the set is small enough
        /// to hand over whole. Legitimate and common, and the filter must still narrow it even though
        /// there is no longer a queryable to push into.
        /// </summary>
        public async Task<IEnumerable<Parcel>> MaterializedParcels(CustomQueryArgs _)
            => await _session.Query<Parcel>().ToListAsync();

        /// <summary>
        /// The author takes over all of filtering, searching, sorting, counting and paging. Returns a
        /// deliberately un-narrowed page so that any framework narrowing is visible as a row count.
        /// </summary>
        public SparkQueryPage<Parcel> PagedParcels(CustomQueryArgs args)
        {
            SeenColumns = args.Columns;
            return new SparkQueryPage<Parcel>(
            [
                new Parcel { Id = "paged/1", Label = "paged-one", Region = "eu", Depot = "depots/4" },
                new Parcel { Id = "paged/2", Label = "paged-two", Region = "us", Depot = "depots/4" },
            ], TotalItems: 2);
        }

        /// <summary>What the last author-paged call was handed, so the test can assert it arrived.</summary>
        internal static IReadOnlyList<QueryColumnFilter>? SeenColumns { get; private set; }
    }

    /// <summary>Row rule as a pushdown-capable filter, so the RQL carries a real security predicate.</summary>
    public class SecuredParcelActions : DefaultPersistentObjectActions<SecuredParcel>
    {
        private readonly IAsyncDocumentSession _session;
        public SecuredParcelActions(IEntityMapper entityMapper, IAsyncDocumentSession session) : base(entityMapper)
            => _session = session;

        public override Task<System.Linq.Expressions.Expression<Func<SecuredParcel, bool>>?> GetRowFilterAsync(string action)
            => Task.FromResult<System.Linq.Expressions.Expression<Func<SecuredParcel, bool>>?>(p => p.Owner == "alice");

        public IRavenQueryable<SecuredParcel> AllSecuredParcels(CustomQueryArgs _) => _session.Query<SecuredParcel>();
    }

    public class ParcelContext : SparkContext
    {
        public IRavenQueryable<Parcel> Parcels => Session.Query<Parcel>();
        public IRavenQueryable<SecuredParcel> SecuredParcels => Session.Query<SecuredParcel>();
    }

    private static EntityTypeFile ParcelModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = ParcelTypeId,
            Name = "Parcel",
            ClrType = typeof(Parcel).FullName!,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Parcel.Label), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Parcel.Region), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Parcel.Depot), DataType = "string" },
            ],
        },
    };

    private static EntityTypeFile SecuredParcelModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SecuredParcelTypeId,
            Name = "SecuredParcel",
            ClrType = typeof(SecuredParcel).FullName!,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(SecuredParcel.Label), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(SecuredParcel.Region), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(SecuredParcel.Owner), DataType = "string" },
            ],
        },
    };

    private SparkEndpointFactory<ParcelContext>? _factory;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Parcel { Label = "one", Region = "eu", Depot = "depots/1" });
            await session.StoreAsync(new Parcel { Label = "two", Region = "us", Depot = "depots/1" });
            await session.StoreAsync(new Parcel { Label = "three", Region = "ap", Depot = "depots/2" });

            await session.StoreAsync(new SecuredParcel { Label = "a-eu", Region = "eu", Owner = "alice" });
            await session.StoreAsync(new SecuredParcel { Label = "a-us", Region = "us", Owner = "alice" });
            await session.StoreAsync(new SecuredParcel { Label = "b-eu", Region = "eu", Owner = "bob" });
        });
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private IQueryExecutor Executor()
    {
        _factory = new SparkEndpointFactory<ParcelContext>(Store, [ParcelModel(), SecuredParcelModel()]);
        return _factory.GetService<IQueryExecutor>();
    }

    private static SparkQuery CustomQuery(string method = "AllParcels", string entityType = "Parcel") => new()
    {
        Id = Guid.NewGuid(),
        Name = method,
        Source = $"Custom.{method}",
        EntityType = entityType,
    };

    // --- The reported bug --------------------------------------------------

    [Fact]
    public async Task Filter_narrows_a_custom_query()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(CustomQuery(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["eu"] }]);

        result.Items.Should().ContainSingle("only one parcel is in 'eu'");
        result.TotalItems.Should().Be(1,
            "a count taken before the filter reports the unfiltered set and the pager then claims "
            + "rows the grid will never show");
    }

    [Fact]
    public async Task Filter_narrows_a_parent_scoped_sub_query()
    {
        var executor = Executor();
        var parent = new PersistentObject { Id = "depots/1", Name = "Depot", ObjectTypeId = Guid.NewGuid() };

        var result = await executor.ExecuteQueryAsync(CustomQuery("ParcelsOfDepot"), parent,
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["eu"] }]);

        result.TotalItems.Should().Be(1,
            "depots/1 holds two parcels and only one of them is in 'eu' — this is the production repro");
    }

    [Fact]
    public async Task Excludes_remove_the_named_values_on_a_custom_query()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(CustomQuery(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Excludes = ["eu"] }]);

        result.TotalItems.Should().Be(2);
    }

    [Fact]
    public async Task A_value_matching_no_row_returns_nothing_rather_than_everything()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(CustomQuery(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["nowhere"] }]);

        // The sharp form of the bug: a filter that is dropped returns the FULL set, so a nonsense
        // value is the one input whose correct answer cannot be produced by ignoring the filter.
        result.TotalItems.Should().Be(0);
    }

    // --- Pushdown: the filter must reach the database (D0/D2) --------------

    [Fact]
    public async Task Filter_is_pushed_into_the_database_on_a_custom_query()
    {
        // Attached before the executor resolves: Raven copies the store's handlers into a session at
        // construction, so a later subscription records nothing — which reads as a passing assertion
        // over an empty collection rather than as a broken test.
        using var rql = RqlRecorder.Attach(Store);
        var executor = Executor();

        await executor.ExecuteQueryAsync(CustomQuery(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["eu"] }]);

        rql.Should().NotBeEmpty("the query must have reached the server");
        rql.Should().Contain(q => q.Contains("Region = "),
            "the filter must be translated into RQL — narrowing in C# returns identical rows, so the "
            + "row count cannot tell the two apart");
    }

    [Fact]
    public async Task Filter_composes_after_the_row_security_predicate_on_a_custom_query()
    {
        using var rql = RqlRecorder.Attach(Store);
        var executor = Executor();

        await executor.ExecuteQueryAsync(CustomQuery("AllSecuredParcels", "SecuredParcel"),
            columnFilters: [new QueryColumnFilter { Name = nameof(SecuredParcel.Region), Includes = ["eu"] }]);

        var emitted = rql.Should().Contain(q => q.Contains("where")).Which;

        // Unlike ColumnFilterDisclosureTests, this fixture HAS a row rule, so there is a real security
        // predicate in the RQL to assert adjacency against. That suite asserts the same shape against
        // the search group, which is not the thing the guarantee is about.
        emitted.Should().Contain("Owner = ", "the row rule must still be in the query");
        emitted.Should().Contain("Region = ", "the column filter must be in the query");
        emitted.Should().NotContain("or (Owner",
            "a filter that becomes an alternative to the security predicate turns the rule into an "
            + "option and silently widens the result");
    }

    [Fact]
    public async Task Row_rule_still_removes_rows_under_an_active_filter()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(CustomQuery("AllSecuredParcels", "SecuredParcel"),
            columnFilters: [new QueryColumnFilter { Name = nameof(SecuredParcel.Region), Includes = ["eu"] }]);

        // Two rows are in 'eu'; one of them is bob's. A filter may only narrow what the rule allows.
        result.TotalItems.Should().Be(1);
        result.Items.Should().OnlyContain(po => po.Breadcrumb == "a-eu");
    }

    [Fact]
    public async Task Filter_narrows_an_in_memory_queryable()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(CustomQuery("InMemoryParcels"),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["eu"] }]);

        // A custom query that returns a fixed set has no database to push into, so the predicate runs
        // in process. That is the only thing filtering a fixed set can mean — not a degraded
        // pushdown — and refusing it would punish a legitimate, supported shape.
        result.TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task Filter_narrows_a_plain_enumerable()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(CustomQuery("ListParcels"),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["eu"] }]);

        // Not an IQueryable at all, so there is no provider to compose onto. It is lifted through
        // AsQueryable so the SAME expression runs: one comparison semantic across every shape,
        // rather than a second implementation that could disagree about what a value equals.
        result.TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task Filter_narrows_a_result_materialized_from_the_database()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(CustomQuery("MaterializedParcels"),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["eu"] }]);

        // The shape a developer reaches for when the set is small enough to hand over whole:
        // session.Query<T>().ToListAsync(). The queryable is gone by the time the framework sees the
        // result, so the filter runs in memory — and it must still narrow.
        result.TotalItems.Should().Be(1);
    }

    [Fact]
    public async Task A_raven_backed_query_never_falls_back_to_filtering_in_process()
    {
        using var rql = RqlRecorder.Attach(Store);
        var executor = Executor();

        await executor.ExecuteQueryAsync(CustomQuery(),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["eu"] }]);

        // The guarantee that in-memory filtering must not weaken: where a database query EXISTS, the
        // filter belongs in it. Row counts cannot see the difference — filtering the same rows in C#
        // returns the same rows — so this can only be asserted on the emitted RQL.
        rql.Should().OnlyContain(q => q.Contains("Region = "),
            "every statement issued for a Raven-backed query must carry the filter, including the "
            + "count — a count that skipped it would report more rows than the grid can show");
    }

    [Fact]
    public async Task An_author_paged_query_keeps_its_own_page_and_receives_the_filters()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(CustomQuery("PagedParcels"),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["eu"] }]);

        // SparkQueryPage<T> IS an IEnumerable<T>, so the in-memory branch would happily narrow it —
        // and then the grid would show one row while TotalItems still said two. The framework
        // filtering a page whose total it did not compute is the half-delegated failure the binary
        // authority rule exists to prevent, and it fails invisibly.
        result.TotalItems.Should().Be(2, "the author owns the count");
        result.Items.Count().Should().Be(2, "the author owns the page; the framework must not trim it");

        // Exempt is not the same as ignored: the author is handed the filters and is responsible for
        // honouring them, exactly as they already are for Search.
        ParcelActions.SeenColumns.Should().ContainSingle()
            .Which.Name.Should().Be(nameof(Parcel.Region));
    }

    // --- Distincts (the other half of the same omission) -------------------

    [Fact]
    public async Task Distincts_on_a_custom_query_reflect_another_columns_filter()
    {
        var executor = Executor();

        var result = await executor.GetDistinctValuesAsync(CustomQuery(), nameof(Parcel.Label),
            columnFilters: [new QueryColumnFilter { Name = nameof(Parcel.Region), Includes = ["eu"] }]);

        // Cross-filtering: the panel must not offer a value that yields an empty grid once picked.
        result.Matching.Should().ContainSingle()
            .Which.Label.Should().Be("one");
    }
}
