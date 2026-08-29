using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// Issue #294 — an <c>async</c> custom query gets the same capabilities as its sync twin.
/// </summary>
/// <remarks>
/// Before the fix, <c>ResolveCustomQueryMethod</c> prefixed both capability flags with
/// <c>!isAsync</c>, so an awaited query silently lost declared sorting, row-filter pushdown, search
/// pushdown, index projection and <c>.Include()</c>. Nothing failed and nothing was logged — a
/// declared <c>sortColumns</c> was simply ignored, which reads as a UI bug rather than a framework one.
/// <para>
/// Most assertions here are on the <strong>emitted RQL</strong> rather than on rows. Row counts and
/// even row order cannot distinguish a pushdown from post-materialization work, and for sorting there
/// is no post-materialization fallback at all — so a row-order assertion would pass for the wrong
/// reason the moment someone added one.
/// </para>
/// </remarks>
public class AsyncCustomQueryTests : SparkTestDriver
{
    private static readonly Guid CrewTypeId = Guid.Parse("cccc3333-cccc-cccc-cccc-cccc33333333");
    private static readonly Guid SquadTypeId = Guid.Parse("dddd4444-dddd-dddd-dddd-dddd44444444");

    public class Squad
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class Crew
    {
        public string? Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;

        [Reference(typeof(Squad))]
        public string? Squad { get; set; }
    }

    public class Crews_Overview : AbstractIndexCreationTask<Crew>
    {
        public Crews_Overview()
        {
            Map = crews => from e in crews
                               select new
                               {
                                   e.FirstName,
                                   e.LastName,
                                   e.Squad,
                                   FullName = e.FirstName + " " + e.LastName,
                               };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    /// <summary><c>FullName</c> exists only in the index — it is null unless the projection is applied.</summary>
    [FromIndex(typeof(Crews_Overview))]
    public class VCrew
    {
        public string? Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? Squad { get; set; }
    }

    public class TestContext : SparkContext
    {
        public IRavenQueryable<Crew> Crews => Session.Query<Crew>();
        public IRavenQueryable<Squad> Squads => Session.Query<Squad>();
    }

    /// <summary>
    /// Every shape the executor has to tell apart, declared as similarly as possible so the only
    /// variable is the return type.
    /// </summary>
    public class CrewActions : DefaultPersistentObjectActions<Crew>
    {
        private readonly IAsyncDocumentSession _session;
        public CrewActions(IEntityMapper entityMapper, IAsyncDocumentSession session) : base(entityMapper)
            => _session = session;

        public IRavenQueryable<Crew> SyncRaven() => _session.Query<Crew>();

        public async Task<IRavenQueryable<Crew>> AsyncRaven()
            => await Task.FromResult(_session.Query<Crew>());

        /// <summary>Declared weaker than it is — the common idiom, and the case that discriminates
        /// runtime inference from signature inference.</summary>
        public async Task<IQueryable<Crew>> AsyncDeclaredQueryable()
            => await Task.FromResult<IQueryable<Crew>>(_session.Query<Crew>());

        /// <summary>The same weakness, without async — a pre-existing gap the fix also closes.</summary>
        public IQueryable<Crew> SyncDeclaredQueryable() => _session.Query<Crew>();

        /// <summary>Already materialized. Must stay non-queryable, which is the boundary
        /// <c>!isAsync</c> was holding by accident.</summary>
        public async Task<IEnumerable<Crew>> AsyncEnumerable()
            => await _session.Query<Crew>().ToListAsync();

        /// <summary>Projection over the index, so <c>FullName</c> is only populated if the
        /// projection gate fires.</summary>
        public async Task<IRavenQueryable<VCrew>> AsyncProjection()
            => await Task.FromResult(_session.Query<VCrew, Crews_Overview>());

        /// <summary>Not a shape the executor supports — pins the diagnostic, not the capability.</summary>
        public ValueTask<IQueryable<Crew>> ValueTaskQuery()
            => new(_session.Query<Crew>());
    }

    private static EntityTypeFile SquadModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = SquadTypeId,
            Name = "Squad",
            ClrType = typeof(Squad).FullName!,
            Breadcrumb = "{Name}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Name", DataType = "string" },
            ],
        },
    };

    private static EntityTypeFile CrewModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = CrewTypeId,
            Name = "Crew",
            ClrType = typeof(Crew).FullName!,
            Breadcrumb = "{LastName}",
            Attributes = [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FirstName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "LastName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "FullName", DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = "Squad", DataType = "Reference", ReferenceType = typeof(Squad).FullName },
            ],
        },
    };

    private SparkEndpointFactory<TestContext> _factory = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new Crews_Overview().ExecuteAsync(Store);
        _factory = new SparkEndpointFactory<TestContext>(Store, [CrewModel(), SquadModel()],
            configureIndexCatalog: catalog =>
            {
                catalog.RegisterIndex(typeof(Crews_Overview));
                catalog.RegisterProjection(typeof(VCrew), typeof(Crews_Overview));
            });
    }

    public override async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        var squad = new Squad { Name = "Engineering" };
        await base.SeedAsync(async session =>
        {
            await session.StoreAsync(squad);
            await session.StoreAsync(new Crew { FirstName = "Ada", LastName = "Lovelace", Squad = squad.Id });
            await session.StoreAsync(new Crew { FirstName = "Grace", LastName = "Hopper", Squad = squad.Id });
            await session.StoreAsync(new Crew { FirstName = "Linus", LastName = "Torvalds", Squad = squad.Id });
        });
        await Store.WaitForIndexingAsync();
    }

    private static SparkQuery Query(string method, SortColumn[]? sortColumns = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = method,
        Source = $"Custom.{method}",
        EntityType = "Crew",
        SortColumns = sortColumns ?? [],
    };

    /// <summary>
    /// Subscribes before the executor is resolved — RavenDB copies the store's handlers into a session
    /// at construction time, so a later subscription never fires.
    /// </summary>
    private (IQueryExecutor Executor, RqlRecorder Rql) Capture()
    {
        var recorder = RqlRecorder.Attach(Store);
        return (_factory.GetService<IQueryExecutor>(), recorder);
    }

    [Fact]
    public async Task An_async_custom_query_applies_declared_sortColumns()
    {
        // RED before the fix: the gate is skipped, and there is no post-materialization sort
        // anywhere, so the declared column is simply discarded.
        await SeedAsync();
        var (executor, rql) = Capture();
        using var _rql = rql;

        var result = await executor.ExecuteQueryAsync(
            Query(nameof(CrewActions.AsyncRaven), [new SortColumn { Property = "LastName", Direction = "asc" }]));

        result.TotalItems.Should().Be(3);
        rql.Should().ContainSingle().Which.Should().Contain("order by LastName");
    }

    [Fact]
    public async Task An_async_IRavenQueryable_custom_query_executes_on_the_async_path()
    {
        // Before the fix this threw: it failed the flag test on the Raven branch and fell into a
        // blocking ToList() over an async session, which RavenDB rejects.
        await SeedAsync();
        var (executor, _) = Capture();

        var result = await executor.ExecuteQueryAsync(Query(nameof(CrewActions.AsyncRaven)));

        result.TotalItems.Should().Be(3);
    }

    [Fact]
    public async Task An_async_custom_query_pushes_the_search_into_the_query()
    {
        await SeedAsync();
        var (executor, rql) = Capture();
        using var _rql = rql;

        var result = await executor.ExecuteQueryAsync(Query(nameof(CrewActions.AsyncRaven)), search: "grace");

        result.TotalItems.Should().Be(1);
        rql.Should().ContainSingle().Which.Should().Contain("search(");
    }

    [Fact]
    public async Task An_async_projection_custom_query_returns_computed_index_fields()
    {
        // FullName exists only in the index. Without the projection gate RavenDB loads the full
        // document, and the computed field comes back empty — no error, no warning.
        await SeedAsync();
        var (executor, _) = Capture();

        var result = await executor.ExecuteQueryAsync(Query(nameof(CrewActions.AsyncProjection)));

        result.TotalItems.Should().Be(3);
        result.Items
            .Select(po => po.Values.Single(a => a.Key == "FullName").Value?.ToString())
            .Should().BeEquivalentTo(["Ada Lovelace", "Grace Hopper", "Linus Torvalds"]);
    }

    [Fact]
    public async Task An_async_custom_query_declared_as_IQueryable_but_backed_by_Raven_uses_the_async_path()
    {
        // The discriminator between inferring from the runtime result and merely deleting !isAsync.
        // Signature inference gives this IsQueryable but not IsRavenQueryable, so it would still miss
        // search pushdown and still materialize through the blocking path.
        await SeedAsync();
        var (executor, rql) = Capture();
        using var _rql = rql;

        var result = await executor.ExecuteQueryAsync(
            Query(nameof(CrewActions.AsyncDeclaredQueryable)), search: "grace");

        result.TotalItems.Should().Be(1);
        rql.Should().ContainSingle().Which.Should().Contain("search(");
    }

    [Fact]
    public async Task A_sync_custom_query_declared_as_IQueryable_but_backed_by_Raven_uses_the_async_path()
    {
        // The same gap, without async. Pre-existing, and closed by the same change.
        await SeedAsync();
        var (executor, rql) = Capture();
        using var _rql = rql;

        var result = await executor.ExecuteQueryAsync(
            Query(nameof(CrewActions.SyncDeclaredQueryable)), search: "grace");

        result.TotalItems.Should().Be(1);
        rql.Should().ContainSingle().Which.Should().Contain("search(");
    }

    [Fact]
    public async Task A_task_of_IEnumerable_custom_query_is_not_treated_as_queryable()
    {
        // The boundary !isAsync was holding by accident: an already-materialized result must not be
        // handed to the Raven path, and a declared sort must not silently appear to work.
        await SeedAsync();
        var (executor, rql) = Capture();
        using var _rql = rql;

        var result = await executor.ExecuteQueryAsync(
            Query(nameof(CrewActions.AsyncEnumerable), [new SortColumn { Property = "LastName", Direction = "asc" }]));

        result.TotalItems.Should().Be(3);
        rql.Should().ContainSingle().Which.Should().NotContain("order by");
    }

    [Fact]
    public async Task A_ValueTask_custom_query_reports_an_accurate_diagnostic()
    {
        // Before the fix: "not found", for a method that plainly exists.
        await SeedAsync();
        var (executor, _) = Capture();

        var act = () => executor.ExecuteQueryAsync(Query(nameof(CrewActions.ValueTaskQuery)));

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("shape the executor cannot use");
        thrown.Which.Message.Should().Contain("ValueTask is not supported");
        thrown.Which.Message.Should().NotContain("not found");
    }
}
