using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// SP4: free-text search over a query that runs through a STATIC index.
/// </summary>
/// <remarks>
/// <c>ResolveSearchableProperties</c> picks every readable <see cref="string"/> CLR property of the
/// element type and <c>ApplySearch</c> emits one <c>search()</c> clause per property. A static index
/// only knows the fields its map emits, and RavenDB refuses a query naming any other field outright —
/// so a single unmapped string property turns every search on that query into an HTTP 500.
/// <para>
/// Measured on RavenDB 7.2.6:
/// <c>System.ArgumentException: The field 'SecretToken' is not indexed in 'Sp4Gadgets/Overview',
/// cannot query/sort on fields that are not indexed in query: from index 'Sp4Gadgets/Overview'
/// where (Owner = $p0) and (search(Label, $p1, and) or search(Owner, $p2, and) or search(SecretToken, $p3, and))</c>
/// </para>
/// <para>
/// The unmapped property is not exotic: <c>[IgnoreForIndex]</c> exists precisely to keep a field out
/// of the index, and the production repro — the Repositories sub-query on a CodeCoverage Account —
/// hits it through <c>Repository.BadgeToken</c>.
/// </para>
/// </remarks>
public class StaticIndexSearchTests : SparkTestDriver
{
    private static readonly Guid GadgetTypeId = Guid.Parse("cccc4444-cccc-cccc-cccc-cccc44444444");

    public class Sp4Gadget
    {
        public string? Id { get; set; }
        public string Label { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;

        /// <summary>Deliberately NOT mapped by the index below — the <c>[IgnoreForIndex]</c> shape.</summary>
        public string? SecretToken { get; set; }
    }

    /// <summary>
    /// Distinctive prefix on purpose: an index class name colliding with another in this assembly
    /// silently redefines the deployed index and breaks unrelated tests.
    /// </summary>
    public class Sp4Gadgets_Overview : AbstractIndexCreationTask<Sp4Gadget>
    {
        public Sp4Gadgets_Overview()
        {
            Map = gadgets => from g in gadgets select new { g.Label, g.Owner };
            StoreAllFields(FieldStorage.Yes);
        }
    }

    public class Sp4GadgetActions : DefaultPersistentObjectActions<Sp4Gadget>
    {
        private readonly IAsyncDocumentSession _session;
        public Sp4GadgetActions(IEntityMapper entityMapper, IAsyncDocumentSession session) : base(entityMapper)
            => _session = session;

        /// <summary>The production shape: a parent-scoped sub-query through a static index.</summary>
        public IRavenQueryable<Sp4Gadget> GadgetsOfOwner(CustomQueryArgs args)
            => _session.Query<Sp4Gadget, Sp4Gadgets_Overview>().Where(g => g.Owner == args.Parent!.Id);

        /// <summary>Control: the same rows with no static index, which is why it searches fine today.</summary>
        public IRavenQueryable<Sp4Gadget> GadgetsOfOwnerNoIndex(CustomQueryArgs args)
            => _session.Query<Sp4Gadget>().Where(g => g.Owner == args.Parent!.Id);
    }

    public class Sp4Context : SparkContext
    {
        public IRavenQueryable<Sp4Gadget> Gadgets => Session.Query<Sp4Gadget>();
    }

    private static EntityTypeFile GadgetModel(bool bindIndex = false) => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = GadgetTypeId,
            Name = "Sp4Gadget",
            ClrType = typeof(Sp4Gadget).FullName!,
            IndexName = bindIndex ? "Sp4Gadgets_Overview" : null,
            Breadcrumb = "{Label}",
            Attributes =
            [
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Sp4Gadget.Label), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Sp4Gadget.Owner), DataType = "string" },
                new EntityAttributeDefinition { Id = Guid.NewGuid(), Name = nameof(Sp4Gadget.SecretToken), DataType = "string" },
            ],
        },
    };

    private SparkEndpointFactory<Sp4Context>? _factory;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new Sp4Gadget { Label = "alpha widget", Owner = "owners/1", SecretToken = "tok-a" });
            await session.StoreAsync(new Sp4Gadget { Label = "beta widget", Owner = "owners/1", SecretToken = "tok-b" });
            await session.StoreAsync(new Sp4Gadget { Label = "gamma widget", Owner = "owners/2", SecretToken = "tok-c" });
        });

        await new Sp4Gadgets_Overview().ExecuteAsync(Store);
        await RavenIndexHelper.WaitForNonStaleAsync(Store);
    }

    public override async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private IQueryExecutor Executor(bool bindIndex = false)
    {
        _factory = new SparkEndpointFactory<Sp4Context>(Store, [GadgetModel(bindIndex)],
            configureIndexCatalog: catalog => catalog.RegisterIndex(typeof(Sp4Gadgets_Overview)));
        return _factory.GetService<IQueryExecutor>();
    }

    private static SparkQuery CustomQuery(string method) => new()
    {
        Id = Guid.NewGuid(),
        Name = method,
        Source = $"Custom.{method}",
        EntityType = "Sp4Gadget",
    };

    private static PersistentObject Parent() =>
        new() { Id = "owners/1", Name = "Owner", ObjectTypeId = Guid.NewGuid() };

    /// <summary>The reported bug: any search term at all, deterministic 500.</summary>
    [Fact]
    public async Task Search_narrows_a_custom_query_that_runs_through_a_static_index()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(
            CustomQuery("GadgetsOfOwner"), Parent(), search: "alpha");

        result.TotalItems.Should().Be(1,
            "owners/1 holds two gadgets and only one is an 'alpha' — today this throws instead, "
            + "because the search clause names SecretToken, which the index does not map");
    }

    /// <summary>
    /// The same failure on a <c>Database.*</c> query, so this is not a custom-query defect. It bites
    /// whenever the search fields come from the ENTITY type rather than from an index projection —
    /// which is every custom query, and every database query bound to a projection-less index.
    /// </summary>
    [Fact]
    public async Task Search_narrows_a_database_query_bound_to_a_projectionless_index()
    {
        var executor = Executor(bindIndex: true);

        var result = await executor.ExecuteQueryAsync(new SparkQuery
        {
            Id = Guid.NewGuid(),
            Name = "GadgetsDb",
            Source = "Database.Gadgets",
            IndexName = "Sp4Gadgets_Overview",
        }, search: "alpha");

        result.TotalItems.Should().Be(1);
    }

    /// <summary>The control that proves the index is the variable: no index, search works.</summary>
    [Fact]
    public async Task Search_without_a_static_index_narrows_correctly()
    {
        var executor = Executor();

        var result = await executor.ExecuteQueryAsync(
            CustomQuery("GadgetsOfOwnerNoIndex"), Parent(), search: "alpha");

        result.TotalItems.Should().Be(1);
    }
}
